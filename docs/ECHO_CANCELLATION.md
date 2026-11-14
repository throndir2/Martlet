# Echo cancellation

**Status:** Implemented
**Last Updated:** 2026-11-13
**Audience:** Developers and coding agents
**Last verified:** 2026-11-13 against b1688a8de
**Code:** `src/Martlet.Audio/EchoReduction.cs`, `src/Martlet.Audio/Windows/WasapiLoopbackReferenceFactory.cs`, `src/Martlet.EchoCancellation/WebRtcEchoCanceller.cs`, `src/Martlet.Desktop/Pages/Listening/`, `src/Martlet.Desktop/LiveConversationController.TurnCapture.cs`

The **Reduce echo from my speakers** choice removes speaker sound from the microphone during hands-free listening. The choice is on by default and can be changed in Companion › Listening.

The desktop wraps microphone capture with `EchoReducer`. It also reads the selected speaker output through Windows loopback capture. The reducer sends matching speaker and microphone frames to the WebRTC acoustic echo canceller.

The canceller accepts 10 ms frames of 16 kHz mono floating-point audio. It processes the speaker frame first, then cleans the microphone frame. A high-pass filter is on. Noise suppression and gain control are off.

Echo reduction is optional. If the canceller or speaker capture cannot start, Martlet continues microphone capture without echo reduction and reports the reason. The status shows whether echo reduction worked during the latest capture.

The speaker reference is used in memory for echo reduction. This feature does not guarantee that all speaker sound is removed.
