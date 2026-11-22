// Offline tests: numbers, not vibes. No audio device is opened.
#include "../engine/graph.h"
#include <chrono>
#include <cstdio>
#include <functional>
#include <memory>
#include <random>
using namespace dsp;
using namespace param;

static int passed = 0, failed = 0;
static void check(bool ok, const char* name, double value) {
    printf("%s  %-58s %10.4f\n", ok ? "PASS" : "FAIL", name, value);
    ok ? passed++ : failed++;
}

using Buf = std::vector<float>;
static std::mt19937 rng(7);

static Buf tone(float f, float amp, int n) {
    Buf b(2 * n);
    for (int i = 0; i < n; i++) b[2 * i] = b[2 * i + 1] = float(amp * std::sin(2 * 3.141592653589793 * f * i / SR));
    return b;
}
static Buf noise(float amp, int n) {
    std::normal_distribution<float> d(0, amp);
    Buf b(2 * n);
    for (int i = 0; i < n; i++) b[2 * i] = b[2 * i + 1] = d(rng);
    return b;
}
static double rms(const Buf& b, int from, int to, int c = 0) {
    double e = 0;
    for (int i = from; i < to; i++) e += double(b[2 * i + c]) * b[2 * i + c];
    return std::sqrt(e / (to - from));
}
static double dB(double x) { return 20 * std::log10(x + 1e-12); }
static void blocks(Buf& b, const std::function<void(float*, int)>& fn, int size = 480) {
    for (int i = 0, n = int(b.size() / 2); i < n; i += size) fn(&b[2 * i], std::min(size, n - i));
}

static void equalizer() {
    for (float f : {31.f, 125.f, 1000.f, 8000.f, 16000.f})
        for (float g : {-12.f, 12.f}) {
            Equalizer eq; eq.set(0, {1, Bell, f, g, 1.4f});
            Buf b = tone(f, .05f, 48000); blocks(b, [&](float* a, int n) { eq.run(a, n); });
            char name[90]; snprintf(name, sizeof name, "EQ bell %gHz %+gdB measured", f, g);
            const double got = dB(rms(b, 24000, 48000) / (.05 / std::sqrt(2.)));
            check(std::fabs(got - g) < .1, name, got);
        }
    check(std::fabs(Coef::make(LowShelf, 200, 6, .707f).magnitudeDb(20) - 6) < .1, "low shelf plateau", Coef::make(LowShelf, 200, 6, .707f).magnitudeDb(20));
    check(Coef::make(HighPass, 100, 0, .707f).magnitudeDb(25) < -23, "high pass 2 octaves down", Coef::make(HighPass, 100, 0, .707f).magnitudeDb(25));
    float worst = 0;
    for (int t = 0; t < 6; t++) for (float f = 10; f < 24000; f *= 1.3f) for (float q : {.1f, .7f, 5.f, 30.f}) for (float g : {-18.f, 18.f}) {
        const Coef c = Coef::make(t, f, g, q);
        worst = std::max(worst, std::fabs(c.a2));
        if (std::fabs(c.a1) > 1 + c.a2 + 1e-6f) worst = 99;
    }
    check(worst <= 1, "every reachable filter is stable", worst);
}

static void dynamics() {
    for (float amp : {1.f, 4.f, 100.f, 10000.f}) {
        Limiter l; Buf b = noise(amp, 48000); blocks(b, [&](float* a, int n) { l.run(a, n, .891f); });
        check(peak(b.data(), int(b.size())) <= .8911f, "limiter never exceeds ceiling", peak(b.data(), int(b.size())));
    }
    { Limiter l; Buf b = tone(1000, .4f, 48000); blocks(b, [&](float* a, int n) { l.run(a, n, .891f); });
      check(std::fabs(dB(rms(b, 4800, 48000) / (.4 / std::sqrt(2.)))) < .01, "limiter transparent below ceiling (dB)", dB(rms(b, 4800, 48000) / (.4 / std::sqrt(2.)))); }
    { Limiter l; Buf b = tone(1000, .1f, 4800); b[200] = NAN; b[401] = INFINITY; blocks(b, [&](float* a, int n) { l.run(a, n, .89f); });
      bool ok = true; for (float v : b) ok &= std::isfinite(v) && std::fabs(v) <= .891f;
      check(ok, "NaN/Inf cannot reach the ear", ok); }
    for (float amp : {.03f, .5f}) {
        Leveler l; Buf b = tone(500, amp, 48000 * 10); blocks(b, [&](float* a, int n) { l.run(a, n, 1, -20); });
        const double got = dB(rms(b, 48000 * 9, 48000 * 10));
        check(std::fabs(got + 20) < 1, "smart volume converges to -20 dB", got);
    }
    { Gate g; Buf b = tone(300, .001f, 48000); blocks(b, [&](float* a, int n) { g.run(a, n, -40, false); });
      check(dB(rms(b, 40000, 48000) / (.001 / std::sqrt(2.))) < -40, "gate closes on room noise", dB(rms(b, 40000, 48000) / (.001 / std::sqrt(2.)))); }
    { Gate g; Buf b = tone(300, .2f, 48000); blocks(b, [&](float* a, int n) { g.run(a, n, -40, false); });
      check(std::fabs(dB(rms(b, 24000, 48000) / (.2 / std::sqrt(2.)))) < .1, "gate open on voice", dB(rms(b, 24000, 48000) / (.2 / std::sqrt(2.)))); }
}

static void denoiser() {
    const int n = 48000 * 8;
    { auto d = std::make_unique<Denoiser>(); Buf in = noise(.1f, 48000), b = in; blocks(b, [&](float* a, int m) { d->run(a, m, 0, 0); });
      double e = 0, s = 0;
      for (int i = 4096; i < 48000; i++) { const double x = in[2 * (i - 512)]; e += (b[2 * i] - x) * (b[2 * i] - x); s += x * x; }
      check(10 * std::log10(e / s + 1e-20) < -80, "STFT perfect reconstruction (dB error)", 10 * std::log10(e / s + 1e-20)); }
    { auto d = std::make_unique<Denoiser>(); Buf in = noise(.01f, n), b = in; blocks(b, [&](float* a, int m) { d->run(a, m, .8f, 0); });
      const double cut = dB(rms(b, n - 96000, n) / rms(in, n - 96000, n));
      check(cut < -15, "stationary noise reduced (dB)", cut); }
    { Buf clean(2 * n);
      for (int i = 0; i < n; i++) {
          const double t = i / 48000., env = t < .5 ? 0 : std::max(0., std::sin(2 * 3.141592653589793 * 2.2 * t));
          double v = 0;
          for (int h = 1; h < 16; h++) v += std::sin(2 * 3.141592653589793 * 160 * h * t) / h;
          clean[2 * i] = clean[2 * i + 1] = float(.06 * env * v);
      }
      Buf mix = noise(.015f, n); for (int i = 0; i < 2 * n; i++) mix[i] += clean[i];
      Buf b = mix; auto d = std::make_unique<Denoiser>(); blocks(b, [&](float* a, int m) { d->run(a, m, .7f, 0); });
      double s = 0, before = 0, after = 0;
      for (int i = n / 2; i < n; i++) {
          const double c = clean[2 * (i - 512)], x = mix[2 * (i - 512)] - c, y = b[2 * i] - c;
          s += c * c; before += x * x; after += y * y;
      }
      const double gain = 10 * std::log10(before / after);
      check(gain > 5, "speech-like SNR improvement (dB)", gain); }
}

static void spatial() {
    auto level = [](int ch, int ear) {
        auto s = std::make_unique<Spatializer>();
        Buf in(SURROUND * 9600), out(2 * 9600);
        for (int i = 0; i < 9600; i++) in[SURROUND * i + ch] = float(.2 * std::sin(2 * 3.141592653589793 * 3000 * i / SR));
        for (int i = 0; i < 9600; i += 480) s->run(&in[SURROUND * i], &out[2 * i], 480, 0, 0);
        return rms(out, 4800, 9600, ear);
    };
    check(dB(level(7, 1) / level(7, 0)) > 6, "source at right: ILD (dB)", dB(level(7, 1) / level(7, 0)));
    check(dB(level(6, 0) / level(6, 1)) > 6, "source at left: ILD (dB)", dB(level(6, 0) / level(6, 1)));
    check(std::fabs(dB(level(2, 0) / level(2, 1))) < .05, "centre is centred (dB)", dB(level(2, 0) / level(2, 1)));
    for (float immersion : {.35f, 1.f}) {
        auto sp = std::make_unique<Spatializer>();
        Buf mono = noise(.1f, 96000), in(SURROUND * 96000), out(2 * 96000);
        for (int i = 0; i < 96000; i++) in[SURROUND * i] = in[SURROUND * i + 1] = mono[2 * i];
        for (int i = 0; i < 96000; i += 480) sp->run(&in[SURROUND * i], &out[2 * i], 480, immersion, .3f);
        const double bal = dB(rms(out, 9600, 96000, 0) / rms(out, 9600, 96000, 1));
        check(std::fabs(bal) < .3, "phantom centre balanced with reflections (dB)", bal);
    }
    Spatializer s;
    const double itd = (s.path[6][1].delay - s.path[6][0].delay) / 48.;
    check(itd > .55 && itd < .75, "ITD at 90 degrees (ms)", itd);
}

static void clocks() {
    for (int margin : {96, 480}) for (double drift : {-3e-4, 0., 3e-4}) {
        Fifo f(2, margin, 2880); float in[2 * 600]{}, out[2 * 480];
        double written = 0; int misses = 0;
        for (int b = 0; b < 60000; b++) { // ten minutes
            written += 480 * (1 + drift);
            const int n = int(written); written -= n;
            f.push(in, n);
            if (!f.pop(out, 480) && b > 100) misses++;
        }
        check(misses <= 3 && f.latency.load() < 2400, "10 min clock drift: no dropouts (fill)", f.latency.load());
    }
}

static void pauses() {
    {   Fifo f(2, 96, 2400); float b[2 * 480]{};
        for (int round = 0; round < 60; round++) {
            for (int i = 0; i < 40; i++) { f.push(b, 480); f.pop(b, 480); }
            for (int i = 0; i < 60; i++) f.pop(b, 480); // the app pauses
        }
        check(f.margin == 96, "60 pauses do not grow the latency", f.margin); }
}

static void mixer() {
    auto s = std::make_unique<graph::State>(); auto m = std::make_unique<graph::Mixer>(); auto io = std::make_unique<graph::Mixer::Io>();
    s->set(StreamerOn, 0, 1); s->set(Vol, Game, .5f); s->set(StreamVol, Game, .25f);
    double main = 0, stream = 0;
    for (int b = 0; b < 100; b++) {
        std::memset(io.get(), 0, sizeof *io);
        for (int i = 0; i < 480; i++) io->app[Game][SURROUND * i] = .2f;
        m->run(*s, *io, 480); main = io->main[800]; stream = io->stream[800];
    }
    check(std::fabs(main - .1) < .001, "personal mix fader", main);
    check(std::fabs(stream - .05) < .001, "stream mix is independent", stream);

    s->set(Sidetone, 0, .25f);
    for (int b = 0; b < 100; b++) {
        std::memset(io.get(), 0, sizeof *io);
        for (float& v : io->mic) v = .2f;
        m->run(*s, *io, 480); main = io->main[800];
    }
    check(std::fabs(main) < .001, "slow path carries no second copy of the sidetone", main);

    for (int c = 0; c < 4; c++) {
        s->set(SpatialOn, c, 1); s->set(DenoiseOn, c, 1); s->set(SmartOn, c, 1); s->set(Boost, c, 6);
        for (int b = 0; b < 10; b++) { const float v[5] = {1, Bell, 100.f * (b + 1), 3, 1}; for (int k = 0; k < 5; k++) s->band[c][b][k] = v[k]; }
    }
    Buf src = noise(.05f, 480 * 4);
    const auto t0 = std::chrono::steady_clock::now();
    for (int b = 0; b < 1000; b++) {
        for (auto& a : io->app) std::memcpy(a, src.data(), sizeof a);
        m->run(*s, *io, 480);
    }
    const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - t0).count() / 1000;
    check(ms < 2.5, "everything on: ms per 10 ms block", ms);
}

static void sidetone() {
    auto s = std::make_unique<graph::State>();
    s->set(Sidetone, 0, .25f);
    // Everything that delays the processed microphone is on.
    for (int id : {NsAmount, ImpactAmount, CompOn, StabilizerOn}) s->set(id, 0, 1);
    // Two identical runs, one with a click: it must come out on the sample it went in.
    auto a = std::make_unique<graph::MonitorChain>(), b = std::make_unique<graph::MonitorChain>();
    float x[960], y[960];
    for (int k = 0; k < 100; k++) {
        for (int i = 0; i < 480; i++) x[2 * i] = x[2 * i + 1] = y[2 * i] = y[2 * i + 1] = .05f * std::sin(.13f * float(i + 480 * k));
        if (k == 99) { y[400] += .01f; y[401] += .01f; }
        a->run(*s, x, 480); b->run(*s, y, 480);
    }
    check(std::fabs(x[400] - y[400]) > 1e-4, "sidetone adds zero samples of delay", std::fabs(x[400] - y[400]));

    auto level = [&](auto change) {
        auto t = std::make_unique<graph::State>(); auto m = std::make_unique<graph::MonitorChain>();
        t->set(Sidetone, 0, 1); change(*t);
        float top = 0, z[960];
        for (int k = 0; k < 100; k++) {
            for (int i = 0; i < 480; i++) z[2 * i] = z[2 * i + 1] = .9f * std::sin(.13f * float(i + 480 * k));
            m->run(*t, z, 480);
            if (k > 80) top = std::max(top, peak(z, 960));
        }
        return top;
    };
    float v = level([](auto&) {});
    check(v > .5, "sidetone reaches the headphones", v);
    v = level([](auto& t) { t.set(Ceiling, 0, -12); t.set(MicGain, 0, 24); });
    check(v > .1 && v <= db(-12) + 1e-5, "sidetone obeys the safety ceiling", v);
    v = level([](auto& t) { t.set(Mute, Mic, 1); });
    check(v < 1e-4, "mic mute silences the sidetone", v);
    v = level([](auto& t) { t.set(MasterMute, 0, 1); });
    check(v < 1e-4, "master mute silences the sidetone", v);
    v = level([](auto& t) { t.set(StreamerOn, 0, 1); t.set(StreamListen, 0, 1); });
    check(v < 1e-4, "no double sidetone while listening to the stream", v);
}

int main() {
    equalizer(); dynamics(); denoiser(); spatial(); clocks(); pauses(); mixer(); sidetone();
    printf("\n%d passed, %d failed\n", passed, failed);
    return failed ? 1 : 0;
}
