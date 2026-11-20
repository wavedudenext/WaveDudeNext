# macOS app of the Avalonia head

Builds `WaveDude Next.app`, a `.dmg` and a `.zip` of `WaveDudeNext.App` (a universal app: Apple silicon and Intel).

    Packaging/Mac/build-app.sh [arm64|x64|universal]        # on a Mac with the .NET 10 SDK and Xcode's command line tools

or run the **macOS build** workflow (Actions tab) and download the artifact.

The app is signed ad hoc, not notarised: the first time, right-click > Open (or `xattr -dr com.apple.quarantine "WaveDude Next.app"`).
For a release without that prompt, set `SIGN_IDENTITY="Developer ID Application: ..."` and notarise the result
(`xcrun notarytool submit ... --wait`, then `xcrun stapler staple`); that needs the distributor's Apple Developer account.

Minimum macOS 14 (what .NET 10 supports). If the app's menu bar shows "Avalonia Application", set `Name` on the `Application`
in `App.axaml.cs`.
