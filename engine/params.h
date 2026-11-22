// Parameter ids shared with ui/Native.cs (enum P). Append only.
#pragma once

namespace param {
enum Id {
    // engine (settings are saved by position, so slot 2 stays reserved)
    MixerOn, MicOn, Retired2, MasterVol, MasterMute, Ceiling,
    // per channel: 0 game, 1 chat, 2 media, 3 aux, 4 mic, 5 master / headphone
    Vol, Mute, StreamVol, StreamMute, Boost, Bass, Voice, Treble, EqOn, Preamp,
    SmartOn, SmartAmount, SmartTarget, DenoiseOn, DenoiseAmount,
    SpatialOn, Immersion, Distance, SpeakerAngle,
    // mixer extras
    ChatMixOn, ChatMix, StreamerOn, StreamListen, SpatialTest,
    // microphone
    MicGain, NsAmount, ImpactAmount, GateOn, GateAuto, GateThreshold,
    CompOn, CompAmount, StabilizerOn, Sidetone, MicTest,
    COUNT
};
constexpr int INDICES = 8;

enum Channel { Game, Chat, Media, Aux, Mic, Master, CHANNELS };
enum Role { OutMain, OutStream, OutMic, OutGame, OutChat, OutMedia, OutAux, InMic, ROLES };

enum Meter {
    MGameL, MGameR, MChatL, MChatR, MMediaL, MMediaR, MAuxL, MAuxR, MMicL, MMicR,
    MOutL, MOutR, MStreamL, MStreamR,
    MMicReduction, MMixerRunning, MMainRunning, MMicRunning,
    MCaptured, MTestState, MSpeaker, MGuardTrips, MPending, METERS
};
} // namespace param
