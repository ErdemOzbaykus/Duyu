// Real-time DSP. Nothing in here allocates, locks or calls the OS while processing.
#pragma once
#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <complex>
#include <cstdint>
#include <cstring>
#include <vector>

namespace dsp {
constexpr float SR = 48000.f;
constexpr float PI = 3.14159265358979f;
constexpr int SURROUND = 8; // FL FR FC LFE BL BR SL SR
constexpr int MAX_BLOCK = 480;

inline float db(float d) { return std::pow(10.f, d / 20.f); }
inline float toDb(float g) { return 20.f * std::log10(std::max(g, 1e-9f)); }
inline float smooth(float ms) { return 1.f - std::exp(-1.f / (ms * .001f * SR)); }
inline float finite(float x) { return std::isfinite(x) ? x : 0.f; }

inline float peak(const float* a, int n) {
    float p = 0;
    for (int i = 0; i < n; i++) p = std::max(p, std::fabs(a[i]));
    return p;
}

// ---------------------------------------------------------------- filters

enum BandType { Bell, LowShelf, HighShelf, LowPass, HighPass, Notch };

struct Coef {
    float b0 = 1, b1 = 0, b2 = 0, a1 = 0, a2 = 0;

    static Coef make(int type, float f, float gainDb, float q) {
        f = std::clamp(f, 10.f, SR * .49f);
        q = std::clamp(q, .1f, 30.f);
        // Double precision: low-frequency biquads lose their shape in float.
        const double A = std::pow(10., gainDb / 40.), w = 2. * 3.141592653589793 * f / SR;
        const double c = std::cos(w), s = std::sin(w), al = s / (2. * q), sq = 2. * std::sqrt(A) * al;
        double b0, b1, b2, a0, a1, a2;
        switch (type) {
        case LowShelf:
            b0 = A * (A + 1 - (A - 1) * c + sq); b1 = 2 * A * (A - 1 - (A + 1) * c); b2 = A * (A + 1 - (A - 1) * c - sq);
            a0 = A + 1 + (A - 1) * c + sq; a1 = -2 * (A - 1 + (A + 1) * c); a2 = A + 1 + (A - 1) * c - sq; break;
        case HighShelf:
            b0 = A * (A + 1 + (A - 1) * c + sq); b1 = -2 * A * (A - 1 + (A + 1) * c); b2 = A * (A + 1 + (A - 1) * c - sq);
            a0 = A + 1 - (A - 1) * c + sq; a1 = 2 * (A - 1 - (A + 1) * c); a2 = A + 1 - (A - 1) * c - sq; break;
        case LowPass:
            b0 = b2 = (1 - c) / 2; b1 = 1 - c; a0 = 1 + al; a1 = -2 * c; a2 = 1 - al; break;
        case HighPass:
            b0 = b2 = (1 + c) / 2; b1 = -1 - c; a0 = 1 + al; a1 = -2 * c; a2 = 1 - al; break;
        case Notch:
            b0 = b2 = 1; b1 = -2 * c; a0 = 1 + al; a1 = -2 * c; a2 = 1 - al; break;
        default:
            b0 = 1 + al * A; b1 = -2 * c; b2 = 1 - al * A; a0 = 1 + al / A; a1 = -2 * c; a2 = 1 - al / A;
        }
        return {float(b0 / a0), float(b1 / a0), float(b2 / a0), float(a1 / a0), float(a2 / a0)};
    }

    float magnitudeDb(float f) const {
        const std::complex<double> z = std::polar(1., -2. * 3.141592653589793 * f / SR);
        auto num = double(b0) + double(b1) * z + double(b2) * z * z;
        auto den = 1. + double(a1) * z + double(a2) * z * z;
        return float(20. * std::log10(std::max(1e-9, std::abs(num / den))));
    }
};

struct Biquad {
    Coef c;
    double z1[2]{}, z2[2]{};

    float run(float x, int ch = 0) {
        const double y = c.b0 * double(x) + z1[ch];
        z1[ch] = c.b1 * double(x) - c.a1 * y + z2[ch];
        z2[ch] = c.b2 * double(x) - c.a2 * y;
        return float(y);
    }
    void stereo(float* a, int n) {
        for (int i = 0; i < n; i++) { a[2 * i] = run(a[2 * i], 0); a[2 * i + 1] = run(a[2 * i + 1], 1); }
    }
};

struct Band { float on = 0, type = Bell, f = 1000, gain = 0, q = 1; };

struct Equalizer {
    static constexpr int N = 10;
    Biquad bands[N], bass, voice, treble;
    bool used[N]{};
    Band last[N]{};
    float tone[3]{};

    void set(int i, const Band& b) {
        if (!std::memcmp(&b, &last[i], sizeof b)) return;
        last[i] = b;
        used[i] = b.on > .5f && (int(b.type) >= LowPass || std::fabs(b.gain) > .01f);
        if (used[i]) bands[i].c = Coef::make(int(b.type), b.f, b.gain, b.q);
    }
    void setTone(float lo, float mid, float hi) {
        if (lo != tone[0]) bass.c = Coef::make(LowShelf, 120, tone[0] = lo, .707f);
        if (mid != tone[1]) voice.c = Coef::make(Bell, 2200, tone[1] = mid, .8f);
        if (hi != tone[2]) treble.c = Coef::make(HighShelf, 6500, tone[2] = hi, .707f);
    }
    void run(float* a, int n) {
        for (int i = 0; i < N; i++) if (used[i]) bands[i].stereo(a, n);
        if (tone[0]) bass.stereo(a, n);
        if (tone[1]) voice.stereo(a, n);
        if (tone[2]) treble.stereo(a, n);
    }
};

// ---------------------------------------------------------------- dynamics

struct Ramp {
    float v = 0;
    void stereo(float* a, int n, float to) {
        const float k = smooth(8);
        for (int i = 0; i < n; i++) { v += (to - v) * k; a[2 * i] *= v; a[2 * i + 1] *= v; }
    }
};

// Look-ahead peak limiter. The final clamp is the hearing-safety backstop.
struct Limiter {
    static constexpr int LA = 64;
    float line[LA * 2]{}, g = 1;
    int at = 0, hold = 0;

    void run(float* a, int n, float ceiling) {
        const float attack = 1.f - std::exp(-6.f / LA), release = smooth(90);
        for (int i = 0; i < n; i++) {
            const float l = finite(a[2 * i]), r = finite(a[2 * i + 1]);
            const float p = std::max(std::fabs(l), std::fabs(r));
            const float need = p > ceiling ? ceiling / p : 1.f;
            if (need < target) { target = need; hold = LA; }
            else if (hold > 0) hold--;
            else target = need;
            g += (target - g) * (target < g ? attack : release);
            a[2 * i] = std::clamp(line[2 * at] * g, -ceiling, ceiling);
            a[2 * i + 1] = std::clamp(line[2 * at + 1] * g, -ceiling, ceiling);
            line[2 * at] = l; line[2 * at + 1] = r;
            at = (at + 1) % LA;
        }
    }
private:
    float target = 1;
};

struct Compressor {
    float env = 0, reduction = 0;

    void run(float* a, int n, float thrDb, float ratio, float makeupDb = 0, float attackMs = 5, float releaseMs = 120) {
        const float thr = db(thrDb), slope = 1.f - 1.f / std::max(1.f, ratio), mk = db(makeupDb);
        const float at = smooth(attackMs), rel = smooth(releaseMs);
        reduction = 0;
        for (int i = 0; i < n; i++) {
            const float p = std::max(std::fabs(a[2 * i]), std::fabs(a[2 * i + 1]));
            env += (p - env) * (p > env ? at : rel);
            const float g = env > thr ? std::pow(thr / env, slope) : 1.f;
            a[2 * i] *= g * mk; a[2 * i + 1] *= g * mk;
            reduction = std::max(reduction, -toDb(g));
        }
    }
};

// Loudness leveller (smart volume / volume stabilizer).
struct Leveler {
    float power = .01f, g = 1;

    void run(float* a, int n, float amount, float targetDb) {
        float e = 0;
        for (int i = 0; i < 2 * n; i++) e += a[i] * a[i];
        e /= float(2 * n);
        const float t = float(n) / SR;
        if (e > 1e-6f) power += (e - power) * (1.f - std::exp(-t / (e > power ? .15f : 1.2f)));
        const float want = e > 1e-6f ? db(std::clamp((targetDb - 10 * std::log10(power + 1e-12f)) * amount, -20.f, 15.f)) : g;
        const float next = g + (want - g) * (1.f - std::exp(-t / (want < g ? .05f : .5f)));
        for (int i = 0; i < n; i++) {
            const float x = g + (next - g) * float(i + 1) / float(n);
            a[2 * i] *= x; a[2 * i + 1] *= x;
        }
        g = next;
    }
};

struct Gate {
    float env = 0, g = 1, floorDb = -60;
    int hold = 0;

    void run(float* a, int n, float thrDb, bool automatic) {
        if (automatic) {
            float e = 0;
            for (int i = 0; i < 2 * n; i++) e += a[i] * a[i];
            const float d = 10 * std::log10(e / float(2 * n) + 1e-10f), t = float(n) / SR;
            floorDb += (d - floorDb) * (d < floorDb ? .15f : std::min(1.f, t * .08f));
            thrDb = std::clamp(floorDb + 14, -70.f, -25.f);
        }
        const float thr = db(thrDb), eRel = smooth(30), open = smooth(1.5f), close = smooth(70);
        for (int i = 0; i < n; i++) {
            const float p = std::max(std::fabs(a[2 * i]), std::fabs(a[2 * i + 1]));
            env = std::max(p, env * (1 - eRel));
            if (env > thr) hold = int(.18f * SR); else if (hold) hold--;
            const float to = hold ? 1.f : .003f;
            g += (to - g) * (to > g ? open : close);
            a[2 * i] *= g; a[2 * i + 1] *= g;
        }
    }
};

// ---------------------------------------------------------------- spectral

struct Fft {
    static constexpr int N = 512;
    int rev[N]{};
    std::complex<float> w[N / 2];

    Fft() {
        for (int i = 0; i < N; i++) for (int b = 0; b < 9; b++) if (i & (1 << b)) rev[i] |= 1 << (8 - b);
        for (int i = 0; i < N / 2; i++) w[i] = std::polar(1.f, -2 * PI * i / N);
    }
    void run(std::complex<float>* a, bool inverse) const {
        for (int i = 0; i < N; i++) if (i < rev[i]) std::swap(a[i], a[rev[i]]);
        for (int len = 2; len <= N; len *= 2)
            for (int i = 0; i < N; i += len)
                for (int j = 0; j < len / 2; j++) {
                    const auto t = inverse ? std::conj(w[j * N / len]) : w[j * N / len];
                    const auto u = a[i + j], v = a[i + j + len / 2] * t;
                    a[i + j] = u + v; a[i + j + len / 2] = u - v;
                }
        if (inverse) for (int i = 0; i < N; i++) a[i] /= float(N);
    }
};
inline const Fft fft;

// MMSE speech-presence noise tracker + decision-directed Wiener filter, with an
// optional transient stage for key and mouse clicks. Classical DSP, no neural model.
struct Denoiser {
    static constexpr int N = Fft::N, H = N / 2, K = H + 1;
    float win[N], in[2][N]{}, out[2][N]{}, noise[K]{}, presence[K]{}, prev[K]{};
    float slow = 0;
    int pos = 0, frames = 0;

    Denoiser() { for (int i = 0; i < N; i++) win[i] = std::sin(PI * (i + .5f) / N); }

    void run(float* a, int n, float amount, float impact) {
        for (int i = 0; i < n; i++) {
            for (int c = 0; c < 2; c++) {
                in[c][H + pos] = finite(a[2 * i + c]);
                a[2 * i + c] = out[c][pos];
            }
            if (++pos == H) { pos = 0; frame(amount, impact); }
        }
    }
private:
    void frame(float amount, float impact) {
        std::complex<float> x[2][N];
        float pw[K], g[K];
        for (int c = 0; c < 2; c++) {
            for (int i = 0; i < N; i++) x[c][i] = in[c][i] * win[i];
            fft.run(x[c], false);
        }
        float total = 0, high = 0;
        for (int k = 0; k < K; k++) {
            pw[k] = .5f * (std::norm(x[0][k]) + std::norm(x[1][k])) + 1e-10f;
            total += pw[k];
            if (k > 32) high += pw[k];
        }
        // Frames of digital silence must not teach the tracker that the room is silent.
        const bool silent = total < 1e-6f;
        if (!silent && frames < 1000) frames++;
        const float floor = db(-34 * amount);
        for (int k = 0; k < K; k++) {
            if (!silent) {
                if (frames <= 8) noise[k] += (pw[k] - noise[k]) / float(frames);
                else {
                    const float xi = 31.62f, post = std::min(pw[k] / noise[k], 100.f);
                    float p = 1 / (1 + (1 + xi) * std::exp(-post * xi / (1 + xi)));
                    presence[k] = .9f * presence[k] + .1f * p;
                    if (presence[k] > .99f) p = std::min(p, .99f);
                    noise[k] = .8f * noise[k] + .2f * ((1 - p) * pw[k] + p * noise[k]);
                }
            }
            const float n2 = noise[k] * (1 + amount) + 1e-10f;
            const float prio = std::max(.96f * prev[k] / n2 + .04f * std::max(pw[k] / n2 - 1, 0.f), .001f);
            g[k] = amount > 0 ? std::max(prio / (1 + prio), floor) : 1.f;
            prev[k] = g[k] * g[k] * pw[k];
        }
        // A click is a sudden rise whose energy sits mostly above 3 kHz.
        const bool click = impact > 0 && total > std::max(slow * 4, 1e-3f) && high > total * .45f;
        slow += (total - slow) * (click ? .02f : .25f);
        if (click) for (float& v : g) v *= db(-30 * impact);

        for (int c = 0; c < 2; c++) {
            for (int k = 0; k < K; k++) {
                x[c][k] *= g[k];
                if (k && k < H) x[c][N - k] = std::conj(x[c][k]);
            }
            fft.run(x[c], true);
            for (int i = 0; i < H; i++) {
                out[c][i] = out[c][i + H] + x[c][i].real() * win[i];
                out[c][i + H] = x[c][i + H].real() * win[i + H];
                in[c][i] = in[c][i + H];
            }
        }
    }
};

// ---------------------------------------------------------------- binaural

// Parametric head model: Woodworth ITD, Brown-Duda head shadow, front/back pinna cues,
// and two cross-fed early reflections. Azimuth is clockwise from the front.
struct Spatializer {
    static constexpr int DL = 4096;
    struct Path { float delay = 0, b0 = 1, b1 = 0, a1 = 0, gain = 1, x = 0, y = 0; } path[SURROUND][2];
    Biquad pinna[SURROUND], notch[SURROUND];
    float angle[SURROUND]{}, line[SURROUND][DL]{}, room[2][DL]{}, damp[2]{};
    int at = 0;

    Spatializer() {
        for (auto& a : angle) a = 1e6f;
        const float def[SURROUND] = {-30, 30, 0, 0, -145, 145, -90, 90};
        for (int i = 0; i < SURROUND; i++) position(i, def[i]);
    }
    void position(int i, float deg) {
        if (deg == angle[i]) return;
        angle[i] = deg;
        const float az = deg * PI / 180, w0 = 343.f / .0875f, k = w0 / SR;
        for (int e = 0; e < 2; e++) {
            const float ear = (e ? 90.f : -90.f) * PI / 180;
            const float inc = std::acos(std::clamp(std::cos(az - ear), -1.f, 1.f));
            const float lead = inc <= PI / 2 ? -std::cos(inc) : inc - PI / 2;
            const float alpha = 1.05f + .95f * std::cos(std::min(inc / (150.f * PI / 180), 1.2f) * PI);
            Path& p = path[i][e];
            p.delay = (1 + lead) * .0875f / 343.f * SR + 1;
            p.b0 = (k + alpha) / (k + 1); p.b1 = (k - alpha) / (k + 1); p.a1 = (k - 1) / (k + 1);
            p.gain = 1 - .22f * inc / PI; // small broadband level difference
        }
        const float front = std::cos(az);
        pinna[i].c = front >= 0 ? Coef::make(Bell, 3800, 2.5f * front, .9f) : Coef::make(HighShelf, 3500, 5 * front, .707f);
        notch[i].c = Coef::make(Bell, front >= 0 ? 8800.f : 7000.f, -3 - 4 * std::max(0.f, -front), 3);
    }
    void run(const float* src, float* dst, int n, float immersion, float distance) {
        const float wet = immersion * (.12f + .3f * distance), dry = .78f * (1 - .25f * distance);
        const int r1 = int(SR * (.007f + .008f * distance)), r2 = int(SR * .023f);
        for (int s = 0; s < n; s++) {
            float ear[2]{};
            for (int i = 0; i < SURROUND; i++) {
                float v = src[SURROUND * s + i];
                if (i == 3) { ear[0] += .5f * v; ear[1] += .5f * v; continue; }
                v = notch[i].run(pinna[i].run(v));
                line[i][at] = v;
                for (int e = 0; e < 2; e++) {
                    Path& p = path[i][e];
                    const int whole = int(p.delay);
                    const float f = p.delay - float(whole);
                    const float x = line[i][(at - whole) & (DL - 1)] * (1 - f) + line[i][(at - whole - 1) & (DL - 1)] * f;
                    p.y = p.b0 * x + p.b1 * p.x - p.a1 * p.y;
                    p.x = x;
                    ear[e] += p.y * p.gain;
                }
            }
            for (int e = 0; e < 2; e++) room[e][at] = ear[e];
            for (int e = 0; e < 2; e++) {
                const float ref = room[1 - e][(at - r1 - e * 37) & (DL - 1)] + .6f * room[e][(at - r2 - e * 53) & (DL - 1)];
                damp[e] += (ref - damp[e]) * .3f;
                dst[2 * s + e] = dry * ear[e] + wet * damp[e];
            }
            at = (at + 1) & (DL - 1);
        }
    }
};

inline void downmix(const float* s, float* d, int n) {
    constexpr float M = .7071f;
    for (int i = 0; i < n; i++, s += SURROUND) {
        d[2 * i] = s[0] + M * (s[2] + s[4] + s[6]) + .5f * s[3];
        d[2 * i + 1] = s[1] + M * (s[2] + s[5] + s[7]) + .5f * s[3];
    }
}

// ---------------------------------------------------------------- transparency helpers

// Catches acoustic feedback: one narrow peak that keeps dominating the spectrum.
struct FeedbackGuard {
    static constexpr int SLOTS = 5;
    float buf[Fft::N]{};
    Biquad notch[SLOTS];
    int left[SLOTS]{}, pos = 0, lastBin = 0, streak = 0, next = 0;

    void run(float* a, int n) {
        for (int i = 0; i < n; i++) {
            buf[pos++] = .5f * (a[2 * i] + a[2 * i + 1]);
            if (pos == Fft::N) { pos = 0; analyse(); }
        }
        for (int i = 0; i < SLOTS; i++)
            if (left[i] > 0) { notch[i].stereo(a, n); left[i] -= n; }
    }
    int active() const {
        int c = 0;
        for (int v : left) c += v > 0;
        return c;
    }
private:
    void analyse() {
        std::complex<float> x[Fft::N];
        for (int i = 0; i < Fft::N; i++) x[i] = buf[i] * (.5f - .5f * std::cos(2 * PI * i / Fft::N));
        fft.run(x, false);
        float best = 0, sum = 0, mag[Fft::N / 2];
        int bin = 0;
        for (int k = 3; k < Fft::N / 2 - 1; k++) {
            mag[k] = std::norm(x[k]);
            sum += mag[k];
            if (mag[k] > best) { best = mag[k]; bin = k; }
        }
        if (bin < 4 || bin >= Fft::N / 2 - 2) { streak = 0; return; }
        const bool howl = best > 4.f && mag[bin - 1] + best + mag[bin + 1] > .9f * sum;
        streak = howl && std::abs(bin - lastBin) <= 1 ? streak + 1 : 0;
        lastBin = bin;
        if (streak < 12) return; // about 130 ms
        streak = 0;
        const float l = std::sqrt(mag[bin - 1]), c = std::sqrt(best), r = std::sqrt(mag[bin + 1]);
        const float f = (bin + .5f * (l - r) / (l - 2 * c + r + 1e-9f)) * SR / Fft::N;
        notch[next].c = Coef::make(Notch, f, 0, 3);
        left[next] = int(12 * SR);
        next = (next + 1) % SLOTS;
    }
};

struct SpeechDetector {
    Biquad hp, lp;
    float floorDb = -55, score = 0;
    int hold = 0;

    SpeechDetector() { hp.c = Coef::make(HighPass, 220, 0, .707f); lp.c = Coef::make(LowPass, 3400, 0, .707f); }
    bool run(const float* a, int n) {
        float band = 0, all = 0;
        for (int i = 0; i < n; i++) {
            const float x = .5f * (a[2 * i] + a[2 * i + 1]), y = lp.run(hp.run(x));
            band += y * y; all += x * x;
        }
        const float d = 10 * std::log10(band / float(n) + 1e-10f), t = float(n) / SR;
        floorDb += (d - floorDb) * (d < floorDb ? .1f : std::min(1.f, t * .06f));
        const bool voiced = d > floorDb + 12 && d > -55 && band > .35f * all;
        score = std::clamp(score + (voiced ? t : -t * .5f), 0.f, .5f);
        if (score > .25f) hold = int(2.5f * SR); else hold = std::max(0, hold - n);
        return hold > 0;
    }
};

// ---------------------------------------------------------------- clock bridge

// SPSC ring between two device clocks. The reader nudges its rate to hold the fill level
// and widens the margin when it runs dry.
struct Fifo {
    static constexpr uint32_t CAP = 1u << 14;
    const int ch, margin0, marginMax;
    std::vector<float> data;
    std::atomic<uint64_t> w{0}, r{0};
    std::atomic<bool> clear{true};
    std::atomic<int> latency{0};
    double frac = 0, avg = 0;
    int margin, marginBefore = 0, dry = 0;
    bool priming = true;

    Fifo(int channels, int marginFrames, int maxMargin)
        : ch(channels), margin0(marginFrames), marginMax(maxMargin), data(size_t(CAP) * channels), margin(marginFrames) {}

    void push(const float* a, int n) { // writer thread
        uint64_t p = w.load(std::memory_order_relaxed);
        if (p + n - r.load(std::memory_order_acquire) > CAP - 4) return;
        for (int i = 0; i < n; i++, p++) std::memcpy(&data[(p & (CAP - 1)) * ch], a + i * ch, sizeof(float) * ch);
        w.store(p, std::memory_order_release);
    }

    bool pop(float* out, int n) { // reader thread
        const uint64_t end = w.load(std::memory_order_acquire);
        uint64_t p = r.load(std::memory_order_relaxed);
        if (clear.exchange(false)) { p = end; frac = 0; priming = true; margin = margin0; }
        int have = int(end - p);
        if (priming) {
            // A long gap is a pause in the audio, not jitter: undo the widening.
            if (dry && ++dry > 20) { margin = marginBefore; dry = 0; }
            if (have < n + margin) { r.store(p, std::memory_order_release); std::memset(out, 0, sizeof(float) * n * ch); return false; }
            p = end - uint64_t(n + margin);
            have = n + margin;
            avg = have; priming = false; dry = 0;
        } else if (have < n + 4) {
            priming = true;
            marginBefore = margin; dry = 1;
            margin = std::min(margin + 96, marginMax);
            std::memset(out, 0, sizeof(float) * n * ch);
            r.store(p, std::memory_order_release);
            return false;
        } else if (have > n + margin + 2400) {
            p = end - uint64_t(n + margin);
            have = n + margin; avg = have;
        }
        avg += (have - avg) * .02;
        latency.store(std::max(0, int(avg) - n), std::memory_order_relaxed);
        // A dead band keeps the bridge bit-transparent while the clocks agree.
        // Writers deliver 480-frame packets, so smaller reads see a saw-tooth fill level.
        const double off = avg - (n + margin + 60 + std::max(0, 480 - n) / 2);
        const double rate = std::fabs(off) < 120 ? 1. : 1. + std::clamp((off - std::copysign(120., off)) * 1e-5, -.003, .003);
        for (int i = 0; i < n; i++) {
            const float f = float(frac);
            const float* a = &data[(p & (CAP - 1)) * ch];
            const float* b = &data[((p + 1) & (CAP - 1)) * ch];
            for (int c = 0; c < ch; c++) out[i * ch + c] = a[c] + (b[c] - a[c]) * f;
            frac += rate;
            const int adv = int(frac);
            p += adv; frac -= adv;
        }
        r.store(p, std::memory_order_release);
        return true;
    }
};
} // namespace dsp
