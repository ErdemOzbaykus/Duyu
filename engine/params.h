// Parameter ids shared with ui/Native.cs (enum P). Append only.
#pragma once

namespace param {
enum Id {
    // engine
    MixerOn, MicOn, TransOn, MasterVol, MasterMute, Ceiling,
    // per channel: 0 game, 1 chat, 2 media, 3 aux, 4 mic, 5 master / headphone
    Vol, Mute, StreamVol, StreamMute, Boost, Bass, Voice, Treble, EqOn, Preamp,
    SmartOn, SmartAmount, SmartTarget, DenoiseOn, DenoiseAmount,
    SpatialOn, Immersion, Distance, SpeakerAngle,
    // mixer extras
    ChatMixOn, ChatMix, StreamerOn, StreamListen, SpatialTest,
    // microphone
    MicGain, NsAmount, ImpactAmount, GateOn, GateAuto, GateThreshold,
    CompOn, CompAmount, StabilizerOn, Sidetone, MicTest,
    // transparency
    TransGain, TransMode, TransTone, TransBalance, TransWidth, TransDenoise,
    TransOwnVoice, TransAware, TransDuck, TransGuard, TransRaw,
    COUNT
};
constexpr int INDICES = 8;

enum Channel { Game, Chat, Media, Aux, Mic, Master, CHANNELS };
enum TransModeId { Natural, Conversation, Awareness };
enum Role { OutMain, OutStream, OutMic, OutGame, OutChat, OutMedia, OutAux, InMic, InTrans, ROLES };

enum Meter {
    MGameL, MGameR, MChatL, MChatR, MMediaL, MMediaR, MAuxL, MAuxR, MMicL, MMicR,
    MOutL, MOutR, MStreamL, MStreamR, MTrans, MTransLatencyMs, MSpeech, MFeedback,
    MMicReduction, MMixerRunning, MMainRunning, MMicRunning, MTransRunning,
    MCaptured, MTestState, MSpeaker, MGuardTrips, MPending, METERS
};
} // namespace param
