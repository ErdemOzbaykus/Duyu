// The signal graph, independent of WASAPI so it can be tested offline.
#pragma once
#include "dsp.h"
#include "params.h"

namespace graph {
using namespace dsp;
using namespace param;

struct State {
    std::atomic<float> v[COUNT][INDICES]{};
    std::atomic<float> band[CHANNELS][Equalizer::N][5]{};
    std::atomic<float> meter[METERS]{};

    float get(int id, int i = 0) const { return v[id][i].load(std::memory_order_relaxed); }
    bool on(int id, int i = 0) const { return get(id, i) > .5f; }
    void set(int id, int i, float x) { v[id][i].store(x, std::memory_order_relaxed); }

    State() {
        set(MasterVol, 0, 1); set(Ceiling, 0, -1);
        for (int c = 0; c < CHANNELS; c++) {
            set(Vol, c, 1); set(StreamVol, c, 1); set(EqOn, c, 1);
            set(SmartAmount, c, .6f); set(SmartTarget, c, -20); set(DenoiseAmount, c, .6f);
            set(Immersion, c, .35f); set(Distance, c, .3f);
        }
        const float angle[SURROUND] = {-30, 30, 0, 0, -145, 145, -90, 90};
        for (int i = 0; i < SURROUND; i++) set(SpeakerAngle, i, angle[i]);
        set(GateAuto, 0, 1); set(GateThreshold, 0, -45); set(CompAmount, 0, .4f);
        set(TransGain, 0, 6); set(TransWidth, 0, 1); set(TransDuck, 0, -15); set(TransGuard, 0, 1); set(TransRaw, 0, 1);
    }
    void loadEq(Equalizer& eq, int c) const {
        for (int i = 0; i < Equalizer::N; i++) {
            auto f = [&](int k) { return band[c][i][k].load(std::memory_order_relaxed); };
            eq.set(i, {f(0), f(1), f(2), f(3), f(4)});
        }
        eq.setTone(get(Bass, c), get(Voice, c), get(Treble, c));
    }
    void meterStereo(int id, const float* a, int n) {
        float l = 0, r = 0;
        for (int i = 0; i < n; i++) { l = std::max(l, std::fabs(a[2 * i])); r = std::max(r, std::fabs(a[2 * i + 1])); }
        meter[id].store(std::max(l, meter[id].load() * .88f));
        meter[id + 1].store(std::max(r, meter[id + 1].load() * .88f));
    }
};

struct AppChannel {
    Spatializer spatial;
    Denoiser denoise;
    Equalizer eq;
    Leveler level;
    Limiter limit;
    bool wasSpatial = false;

    void run(State& s, int c, float* in, float* out, int n) {
        const bool spatialOn = s.on(SpatialOn, c) || (c == Game && s.on(SpatialTest));
        if (spatialOn) {
            for (int i = 0; i < SURROUND; i++) spatial.position(i, s.get(SpeakerAngle, i));
            spatial.run(in, out, n, s.get(Immersion, c), s.get(Distance, c));
        } else downmix(in, out, n);
        if (s.on(DenoiseOn, c)) denoise.run(out, n, s.get(DenoiseAmount, c), 0);
        if (s.on(EqOn, c)) {
            s.loadEq(eq, c);
            eq.run(out, n);
            const float g = db(s.get(Preamp, c));
            if (g != 1) for (int i = 0; i < 2 * n; i++) out[i] *= g;
        }
        if (s.on(SmartOn, c)) level.run(out, n, s.get(SmartAmount, c), s.get(SmartTarget, c));
        const float boost = db(s.get(Boost, c));
        if (boost != 1) {
            for (int i = 0; i < 2 * n; i++) out[i] *= boost;
            limit.run(out, n, .985f);
        }
        s.meterStereo(MGameL + 2 * c, out, n);
    }
};

struct MicChain {
    Biquad rumble;
    Denoiser denoise;
    Gate gate;
    Equalizer eq;
    Compressor comp;
    Leveler level;
    Limiter limit;

    MicChain() { rumble.c = Coef::make(HighPass, 70, 0, .707f); }
    void run(State& s, float* a, int n) {
        rumble.stereo(a, n);
        const float ns = s.get(NsAmount), impact = s.get(ImpactAmount);
        if (ns > 0 || impact > 0) denoise.run(a, n, ns, impact);
        if (s.on(GateOn)) gate.run(a, n, s.get(GateThreshold), s.on(GateAuto));
        if (s.on(EqOn, Mic)) { s.loadEq(eq, Mic); eq.run(a, n); }
        float reduction = 0;
        if (s.on(CompOn)) {
            const float x = s.get(CompAmount);
            comp.run(a, n, -10 - 20 * x, 1.5f + 4.5f * x, 8 * x);
            reduction = comp.reduction;
        }
        if (s.on(StabilizerOn)) level.run(a, n, .8f, -20);
        const float g = db(s.get(MicGain) + s.get(Preamp, Mic)) * (s.on(Mute, Mic) ? 0.f : 1.f);
        for (int i = 0; i < 2 * n; i++) a[i] *= g;
        limit.run(a, n, .89f);
        s.meterStereo(MMicL, a, n);
        s.meter[MMicReduction].store(reduction);
    }
};

// Microphone to ear. Stays sample-synchronous: the only added delay is the 1.3 ms limiter,
// unless the user switches on ambient denoise (+10.7 ms).
struct Transparency {
    Biquad hp, presence, shelf, cut;
    Denoiser denoise;
    Compressor lift, own;
    FeedbackGuard guard;
    SpeechDetector speech;
    Limiter limit;
    int mode = -1;
    float tone = 1e6f, fade = 0;

    void configure(int m, float t) {
        if (m == mode && t == tone) return;
        mode = m; tone = t;
        hp.c = Coef::make(HighPass, m == Conversation ? 180.f : 80.f, 0, .707f);
        presence.c = Coef::make(Bell, 2600, m == Conversation ? 6.f : m == Awareness ? 3.f : 1.5f, .8f);
        cut.c = Coef::make(HighShelf, 7500, m == Conversation ? -5.f : 0.f, .707f);
        shelf.c = Coef::make(HighShelf, 1500, t * 8, .6f);
    }
    void run(State& s, float* a, int n) {
        configure(int(s.get(TransMode)), s.get(TransTone));
        const bool talking = s.on(TransAware) && speech.run(a, n);
        s.meter[MSpeech].store(talking ? 1.f : 0.f);
        hp.stereo(a, n);
        if (s.get(TransDenoise) > 0) denoise.run(a, n, s.get(TransDenoise), 0);
        presence.stereo(a, n); cut.stereo(a, n); shelf.stereo(a, n);
        if (mode == Awareness) lift.run(a, n, -42, 2.5f, 12, 3, 160);
        if (s.get(TransOwnVoice) > 0) own.run(a, n, -22 - 14 * s.get(TransOwnVoice), 8, 0, 2, 180);

        const float g = db(s.get(TransGain)), bal = s.get(TransBalance), width = s.get(TransWidth), k = smooth(30);
        const float gl = g * std::min(1.f, 1 - bal), gr = g * std::min(1.f, 1 + bal);
        for (int i = 0; i < n; i++) {
            fade += (1 - fade) * k; // soft start, so switching on never bangs
            const float m = .5f * (a[2 * i] + a[2 * i + 1]), d = .5f * (a[2 * i] - a[2 * i + 1]) * width;
            a[2 * i] = (m + d) * gl * fade; a[2 * i + 1] = (m - d) * gr * fade;
        }
        if (s.on(TransGuard)) guard.run(a, n);
        limit.run(a, n, .5f); // -6 dBFS: the room can never be louder than this
        s.meter[MTrans].store(std::max(peak(a, 2 * n), s.meter[MTrans].load() * .88f));
        s.meter[MFeedback].store(float(guard.active()));
    }
};

// Everything that happens on the main output clock.
struct Mixer {
    struct Io {
        float app[4][SURROUND * MAX_BLOCK]; // in
        float mic[2 * MAX_BLOCK];           // in
        float trans[2 * MAX_BLOCK];         // in
        float playback[2 * MAX_BLOCK];      // in: microphone test playback
        bool separate[4];                   // in: channel has its own output device
        float main[2 * MAX_BLOCK];          // out
        float stream[2 * MAX_BLOCK];        // out
        float own[4][2 * MAX_BLOCK];        // out
    };
    AppChannel channel[4];
    Ramp personal[4], streamer[5], side, master;
    Equalizer correction;
    Limiter mainLimit, streamLimit, ownLimit[4];
    float duck = 1;
    uint32_t rng = 22222;
    uint64_t clock = 0;

    void run(State& s, Io& io, int n) {
        static constexpr int order[] = {2, 1, 7, 5, 4, 6, 0}; // clockwise around the head
        if (s.on(SpatialTest)) {
            for (int i = 0; i < n; i++) {
                const uint64_t t = clock + i;
                rng = rng * 1664525u + 1013904223u;
                const float env = float(t % 24000) / 24000;
                const float burst = env < .6f ? std::sin(PI * env / .6f) * ((t / 1600) % 2 ? .15f : 1.f) : 0.f;
                io.app[Game][SURROUND * i + order[(t / 24000) % 7]] += .09f * burst * float(int32_t(rng)) / 2147483648.f;
            }
            s.meter[MSpeaker].store(float(order[(clock / 24000) % 7]));
        } else s.meter[MSpeaker].store(-1);
        clock += n;

        const float duckTo = s.on(TransOn) && s.on(TransAware) && s.meter[MSpeech].load() > .5f ? db(s.get(TransDuck)) : 1.f;
        duck += (duckTo - duck) * (1 - std::exp(-float(n) / SR / (duckTo < duck ? .08f : .5f)));

        std::memset(io.main, 0, sizeof(float) * 2 * n);
        std::memset(io.stream, 0, sizeof(float) * 2 * n);
        float work[2 * MAX_BLOCK], tmp[2 * MAX_BLOCK];
        const bool streaming = s.on(StreamerOn), listen = streaming && s.on(StreamListen);
        const float mix = s.on(ChatMixOn) ? s.get(ChatMix) : 0.f; // -1 game .. +1 chat
        const float masterGain = s.on(MasterMute) ? 0.f : s.get(MasterVol);

        for (int c = 0; c < 4; c++) {
            channel[c].run(s, c, io.app[c], work, n);
            std::memcpy(tmp, work, sizeof(float) * 2 * n);
            float g = s.on(Mute, c) ? 0.f : s.get(Vol, c);
            if (c == Game) g *= std::min(1.f, 1 - mix);
            if (c == Chat) g *= std::min(1.f, 1 + mix);
            else g *= duck;
            personal[c].stereo(tmp, n, g);
            if (io.separate[c]) {
                for (int i = 0; i < 2 * n; i++) io.own[c][i] = tmp[i] * masterGain;
                ownLimit[c].run(io.own[c], n, db(s.get(Ceiling)));
            } else for (int i = 0; i < 2 * n; i++) io.main[i] += tmp[i];
            if (streaming) {
                streamer[c].stereo(work, n, s.on(StreamMute, c) ? 0.f : s.get(StreamVol, c));
                for (int i = 0; i < 2 * n; i++) io.stream[i] += work[i];
            }
        }
        if (streaming) {
            std::memcpy(tmp, io.mic, sizeof(float) * 2 * n);
            streamer[4].stereo(tmp, n, s.on(StreamMute, Mic) ? 0.f : s.get(StreamVol, Mic));
            for (int i = 0; i < 2 * n; i++) io.stream[i] += tmp[i];
            streamLimit.run(io.stream, n, .89f);
        }
        if (listen) std::memcpy(io.main, io.stream, sizeof(float) * 2 * n);
        else {
            std::memcpy(tmp, io.mic, sizeof(float) * 2 * n);
            side.stereo(tmp, n, s.get(Sidetone));
            for (int i = 0; i < 2 * n; i++) io.main[i] += tmp[i];
        }
        master.stereo(io.main, n, masterGain);
        // Transparency bypasses master volume: turning music down must not deafen you.
        for (int i = 0; i < 2 * n; i++) io.main[i] += io.trans[i] + io.playback[i];
        if (s.on(EqOn, Master)) {
            s.loadEq(correction, Master);
            correction.run(io.main, n);
            const float g = db(s.get(Preamp, Master));
            for (int i = 0; i < 2 * n; i++) io.main[i] *= g;
        }
        mainLimit.run(io.main, n, db(s.get(Ceiling)));
        s.meterStereo(MOutL, io.main, n);
        s.meterStereo(MStreamL, io.stream, n);
    }
};
} // namespace graph
