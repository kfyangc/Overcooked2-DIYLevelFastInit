# Third-party notices

## ResourceReadFailureBlocker

`ResourceFailures/BlockerLog.cs`, `BlockerPatches.cs`, `BlockerRuntime.cs`,
`FailureDialogLayout.cs`, `FailureGate.cs`, `ModLocalization.cs` and
`PreparationFailureNotice.cs` are adapted from the author-provided
OC2DIYLevel-ResourceReadFailureBlocker source snapshot, identified by its
`PluginInfo.cs` as version 0.4.0.0, author Github@CH3NGY2.
Copyright © Github@CH3NGY2. No separate license file was included in that snapshot.

The integration changes namespaces and lifecycle ownership, connects FastInit's
entry/cancellation gates, adapts resource-consumer overloads to both supported
OC2DIYLevel versions, and replaces closed-generic Harmony hooks with typed
forwarding at non-generic callers. The standalone plugin entry point and identity
are not included. Native dialog layout and localization originate from that source.

## LZMA SDK

The decoder subset in `BundleCodecs/`, under
`DIYLevelFastInit.BundleCodecs.Compression`, is adapted from Igor Pavlov's
LZMA SDK 23.01 C# sources. Only the range decoder, bit decoders, output window,
LZMA constants and LZMA decoder are included. Encoder code is not included.
The adaptation changes namespaces and visibility, removes unused interfaces,
and uses `IOException` for invalid data and properties.

The LZMA SDK is in the public domain. Its author permits copying, modification,
compilation and distribution for any purpose, including commercial use.

Source: [LZMA SDK](https://www.7-zip.org/sdk.html),
[23.01 archive](https://www.7-zip.org/a/lzma2301.7z).
