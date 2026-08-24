using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using QingTab.Helpers;
using QingTab.Hooks;

namespace QingTab.Tests;

internal static class Program
{
    private static readonly List<string> Failures = new();
    private static int Checks;

    private static int Main()
    {
        Check(
            "drive-root parse artifact is repaired",
            @"E:\",
            ShellFolderOpenRequest.NormalizePath("E:" + '"'));

        Check(
            "UNC-root parse artifact is repaired",
            @"\\server\share\",
            ShellFolderOpenRequest.NormalizePath(@"\\server\share" + '"'));

        Check(
            "normal folder path is unchanged",
            @"C:\Folder Name",
            ShellFolderOpenRequest.NormalizePath(@"C:\Folder Name"));

        Check(
            "drive root does not need command-line quotes",
            @"E:\",
            ShellFolderOpenRequest.QuoteArgument(@"E:\"));

        Check(
            "quoted trailing slash is doubled",
            "\"C:\\Folder Name\\\\\"",
            ShellFolderOpenRequest.QuoteArgument(@"C:\Folder Name\"));

        CheckTrue(
            "direct open accepts an absolute drive folder",
            ShellFolderOpenRequest.ShouldHandleDirectOpen(@"C:\Work"));
        CheckTrue(
            "direct open repairs and accepts a quoted drive-root artifact",
            ShellFolderOpenRequest.ShouldHandleDirectOpen("E:" + '"'));
        CheckTrue(
            "direct open accepts a UNC folder without probing the network",
            ShellFolderOpenRequest.ShouldHandleDirectOpen(@"\\server\share\folder"));
        CheckFalse(
            "Recycle Bin CLSID bypasses QingTab",
            ShellFolderOpenRequest.ShouldHandleDirectOpen(
                "::{645FF040-5081-101B-9F08-00AA002F954E}"));
        CheckFalse(
            "Recycle Bin shell alias bypasses QingTab",
            ShellFolderOpenRequest.ShouldHandleDirectOpen("shell:RecycleBinFolder"));
        CheckFalse(
            "Control Panel bypasses QingTab",
            ShellFolderOpenRequest.ShouldHandleDirectOpen(
                "::{26EE0668-A00A-44D7-9371-BEB064C98683}"));
        CheckFalse(
            "Libraries bypass QingTab",
            ShellFolderOpenRequest.ShouldHandleDirectOpen(
                "::{031E4825-7B94-4DC3-B131-E946B44C8DD5}"));
        CheckFalse(
            "relative paths bypass QingTab",
            ShellFolderOpenRequest.ShouldHandleDirectOpen(@"folder\child"));
        CheckFalse(
            "drive-relative paths bypass QingTab",
            ShellFolderOpenRequest.ShouldHandleDirectOpen(@"C:folder"));
        CheckFalse(
            "blank targets bypass QingTab",
            ShellFolderOpenRequest.ShouldHandleDirectOpen("  "));

        var recycleBinStartInfo = ExplorerLauncher.CreateStartInfo(
            "::{645FF040-5081-101B-9F08-00AA002F954E}");
        CheckTrue(
            "Recycle Bin native fallback uses explicit Explorer without Shell recursion",
            recycleBinStartInfo.FileName.EndsWith(
                @"\explorer.exe",
                StringComparison.OrdinalIgnoreCase)
            && recycleBinStartInfo.Arguments
                == "::{645FF040-5081-101B-9F08-00AA002F954E}"
            && !recycleBinStartInfo.UseShellExecute);
        Check(
            "Recycle Bin shell alias is preserved for native Explorer",
            "shell:RecycleBinFolder",
            ExplorerLauncher.CreateStartInfo("shell:RecycleBinFolder").Arguments);

        var diagnostics = new DiagnosticHistory(capacity: 2);
        var discardedTrace = diagnostics.Begin(@"C:\Private\first-folder", new IntPtr(101));
        discardedTrace.Mark(OpenTabStage.QueueStarted);
        discardedTrace.Complete(OpenTabOutcome.OpenedInTab);

        var retainedTrace = diagnostics.Begin(@"E:\Secret Project\second-folder", new IntPtr(202));
        retainedTrace.Mark(OpenTabStage.QueueStarted);
        retainedTrace.Mark(OpenTabStage.TabCommandSent);
        retainedTrace.Mark(OpenTabStage.NavigationStarted);
        retainedTrace.Mark(OpenTabStage.NavigationCompleted);
        retainedTrace.Complete(OpenTabOutcome.OpenedInTab);

        var newestTrace = diagnostics.Begin(@"\\server\share\confidential", IntPtr.Zero);
        newestTrace.Mark(OpenTabStage.QueueStarted);
        newestTrace.Complete(OpenTabOutcome.OpenedInWindowFallback, "shell-registration-timeout");

        var report = diagnostics.BuildReport(
            version: "0.2.2",
            directOpenEnabled: true,
            autoStartEnabled: false);
        CheckTrue("diagnostics keep the configured ring-buffer size", diagnostics.Count == 2);
        CheckTrue("diagnostics report stage timings", report.Contains("发送新标签命令"));
        CheckTrue("diagnostics separates navigation call start from COM return",
            report.Contains("开始导航") && report.Contains("导航返回") && report.Contains("累计"));
        CheckTrue("diagnostics report fallback reason", report.Contains("shell-registration-timeout"));
        CheckTrue("diagnostics report app state", report.Contains("v0.2.2") && report.Contains("开机自启：关闭"));
        CheckFalse("diagnostics never retain a local path", report.Contains("Secret Project"));
        CheckFalse("diagnostics never retain a UNC path", report.Contains("confidential"));
        CheckFalse("old diagnostics are evicted", report.Contains("first-folder"));
        CheckTrue("diagnostics summarize recent latency with P50 and P95",
            report.Contains("P50") && report.Contains("P95"));

        var openedResult = OpenTabResult.Opened();
        CheckTrue("open-tab result exposes a successful tab open",
            openedResult.IsSuccess
            && openedResult.Kind == OpenTabResultKind.Opened
            && string.IsNullOrEmpty(openedResult.FailureCode));

        var openedInBackgroundResult = OpenTabResult.OpenedInBackground();
        CheckTrue("a loaded background tab is handled without opening a duplicate fallback",
            openedInBackgroundResult.IsSuccess
            && openedInBackgroundResult.Kind == OpenTabResultKind.OpenedInBackground
            && !openedInBackgroundResult.ShouldOpenFallback
            && string.IsNullOrEmpty(openedInBackgroundResult.FailureCode));

        var userIntervenedResult = OpenTabResult.Suppressed(
            OpenTabResultKind.UserIntervened);
        CheckTrue("user intervention permanently suppresses a surprise fallback window",
            !userIntervenedResult.IsSuccess
            && !userIntervenedResult.ShouldOpenFallback
            && userIntervenedResult.FailureCode == "user-intervened");

        var unknownNavigationResult = OpenTabResult.Suppressed(
            OpenTabResultKind.NavigationOutcomeUnknown,
            unchecked((int)0x80004005));
        CheckTrue("an unknown navigation outcome never opens a duplicate fallback",
            !unknownNavigationResult.IsSuccess
            && !unknownNavigationResult.ShouldOpenFallback
            && unknownNavigationResult.FailureCode == "navigation-outcome-unknown"
            && unknownNavigationResult.HResult == unchecked((int)0x80004005));

        var unknownTabCreationResult = OpenTabResult.Suppressed(
            OpenTabResultKind.TabCreationOutcomeUnknown);
        CheckTrue("an acknowledged but unclaimed tab command never opens a duplicate fallback",
            !unknownTabCreationResult.IsSuccess
            && !unknownTabCreationResult.ShouldOpenFallback
            && unknownTabCreationResult.FailureCode == "tab-creation-outcome-unknown");

        CheckTrue("visual mask bounds cover the owner client below the tab strip",
            ExplorerVisualMaskBounds.TryCreateBelowTabStrip(
                clientLeft: 100,
                clientTop: 80,
                clientRight: 1_100,
                clientBottom: 900,
                tabListBottom: 133.2,
                out var visualMaskBounds)
            && visualMaskBounds.Left == 100
            && visualMaskBounds.Top == 134
            && visualMaskBounds.Width == 1_000
            && visualMaskBounds.Height == 766
            && visualMaskBounds.MatchesOwnerClient(100, 80, 1_100, 900));
        CheckFalse("visual mask bounds reject a tab strip below the client",
            ExplorerVisualMaskBounds.TryCreateBelowTabStrip(
                100,
                80,
                1_100,
                900,
                900.1,
                out _));
        CheckFalse("visual mask bounds reject non-finite UIA geometry",
            ExplorerVisualMaskBounds.TryCreateBelowTabStrip(
                100,
                80,
                1_100,
                900,
                double.NaN,
                out _));
        CheckFalse("visual mask bounds detect owner geometry drift",
            visualMaskBounds.MatchesOwnerClient(100, 80, 1_101, 900));

        var visualMaskStopPolicy = new ExplorerVisualMaskStopPolicy(hardTimeoutMilliseconds: 4_500);
        CheckFalse("visual mask rejects any lifetime above the 4500 ms release ceiling",
            ExplorerVisualMaskStopPolicy.IsValidHardTimeout(4_501));
        CheckTrue("visual mask remains alive before its hard deadline",
            visualMaskStopPolicy.Evaluate(
                elapsedMilliseconds: 4_499,
                releaseRequested: false,
                ownerIdentityIsValid: true,
                ownerIsForeground: true,
                geometryMatches: true) == ExplorerVisualMaskStopReason.None);
        CheckTrue("visual mask hard deadline is inclusive",
            visualMaskStopPolicy.Evaluate(
                elapsedMilliseconds: 4_500,
                releaseRequested: false,
                ownerIdentityIsValid: true,
                ownerIsForeground: true,
                geometryMatches: true) == ExplorerVisualMaskStopReason.HardTimeout);
        CheckFalse("expired visual mask cannot renew",
            visualMaskStopPolicy.TryRenew(elapsedMilliseconds: 4_500));

        var renewedVisualMaskPolicy = new ExplorerVisualMaskStopPolicy(4_500);
        CheckTrue("live visual mask may acknowledge one bounded renewal without extending its absolute deadline",
            renewedVisualMaskPolicy.TryRenew(elapsedMilliseconds: 500)
            && renewedVisualMaskPolicy.DeadlineMilliseconds == 4_500);
        CheckFalse("visual mask policy cannot be renewed indefinitely",
            renewedVisualMaskPolicy.TryRenew(elapsedMilliseconds: 600));
        CheckTrue("renewed visual mask still stops at the original absolute deadline",
            renewedVisualMaskPolicy.Evaluate(
                elapsedMilliseconds: 4_500,
                releaseRequested: false,
                ownerIdentityIsValid: true,
                ownerIsForeground: true,
                geometryMatches: true,
                userInputUnchanged: true) == ExplorerVisualMaskStopReason.HardTimeout);
        CheckTrue("measured 3238 ms active Explorer navigation remains covered",
            renewedVisualMaskPolicy.Evaluate(
                elapsedMilliseconds: 3_238,
                releaseRequested: false,
                ownerIdentityIsValid: true,
                ownerIsForeground: true,
                geometryMatches: true,
                userInputUnchanged: true) == ExplorerVisualMaskStopReason.None);
        CheckTrue("visual mask fails closed as soon as user input changes",
            renewedVisualMaskPolicy.Evaluate(
                elapsedMilliseconds: 501,
                releaseRequested: false,
                ownerIdentityIsValid: true,
                ownerIsForeground: true,
                geometryMatches: true,
                userInputUnchanged: false) == ExplorerVisualMaskStopReason.UserInputDetected);
        CheckTrue("visual mask stops immediately when owner focus is lost",
            renewedVisualMaskPolicy.Evaluate(
                elapsedMilliseconds: 501,
                releaseRequested: false,
                ownerIdentityIsValid: true,
                ownerIsForeground: false,
                geometryMatches: true,
                userInputUnchanged: true) == ExplorerVisualMaskStopReason.OwnerNotForeground);
        CheckTrue("visual mask stops immediately on owner geometry drift",
            renewedVisualMaskPolicy.Evaluate(
                elapsedMilliseconds: 501,
                releaseRequested: false,
                ownerIdentityIsValid: true,
                ownerIsForeground: true,
                geometryMatches: false,
                userInputUnchanged: true) == ExplorerVisualMaskStopReason.GeometryChanged);
        CheckTrue("visual mask stops immediately when owner identity changes",
            renewedVisualMaskPolicy.Evaluate(
                elapsedMilliseconds: 501,
                releaseRequested: false,
                ownerIdentityIsValid: false,
                ownerIsForeground: true,
                geometryMatches: true,
                userInputUnchanged: true) == ExplorerVisualMaskStopReason.OwnerIdentityChanged);
        CheckTrue("explicit visual mask release has highest stop priority",
            renewedVisualMaskPolicy.Evaluate(
                elapsedMilliseconds: 4_500,
                releaseRequested: true,
                ownerIdentityIsValid: false,
                ownerIsForeground: false,
                geometryMatches: false,
                userInputUnchanged: false) == ExplorerVisualMaskStopReason.Released);

        var visualMaskLeaseState = new ExplorerVisualMaskLeaseState();
        CheckTrue("abandoning a visual mask only disables renewal",
            visualMaskLeaseState.TryAbandonToTimeout()
            && visualMaskLeaseState.IsAbandoned
            && !visualMaskLeaseState.ReleaseRequested
            && !visualMaskLeaseState.CanRenew);
        CheckTrue("dispose can still request immediate release after abandonment",
            visualMaskLeaseState.TryRequestRelease()
            && visualMaskLeaseState.ReleaseRequested);

        var coherentIdentityInitial = new ExplorerTabIdentityPolicySnapshot(
            new[] { new IntPtr(801), new IntPtr(802) },
            new IntPtr(802),
            new[] { "tab-a", "tab-b" },
            "tab-b",
            targetWindowIsForeground: true);
        CheckTrue("dual identity starts only from two coherent native and UIA samples",
            ExplorerTabDualIdentityPolicy.TryCreate(
                coherentIdentityInitial,
                coherentIdentityInitial,
                out var dualIdentityPolicy));

        var coherentIdentityCreated = new ExplorerTabIdentityPolicySnapshot(
            new[] { new IntPtr(803), new IntPtr(801), new IntPtr(802) },
            new IntPtr(803),
            new[] { "tab-a", "tab-b", "tab-new" },
            "tab-new",
            targetWindowIsForeground: true);
        CheckTrue("dual identity binds exactly one active native and selected UIA addition",
            dualIdentityPolicy.ObserveForBinding(
                coherentIdentityCreated,
                coherentIdentityCreated,
                out var coherentBinding) == ExplorerTabIdentityPolicyDecision.Bound
            && coherentBinding != null
            && coherentBinding.CreatedNativeTabHandle == new IntPtr(803)
            && coherentBinding.CreatedRuntimeId == "tab-new");
        CheckTrue("restore is authorized only while the exact bound orders remain selected",
            dualIdentityPolicy.TryAuthorizeOriginalRestore(
                coherentIdentityCreated,
                coherentIdentityCreated));

        CheckFalse("dual identity rejects an incoherent initial foreground sample",
            ExplorerTabDualIdentityPolicy.TryCreate(
                coherentIdentityInitial,
                new ExplorerTabIdentityPolicySnapshot(
                    new[] { new IntPtr(801), new IntPtr(802) },
                    new IntPtr(802),
                    new[] { "tab-a", "tab-b" },
                    "tab-b",
                    targetWindowIsForeground: false),
                out _));

        ExplorerTabDualIdentityPolicy.TryCreate(
            coherentIdentityInitial,
            coherentIdentityInitial,
            out var ambiguousDualIdentityPolicy);
        var twoNativeAdditions = new ExplorerTabIdentityPolicySnapshot(
            new[] { new IntPtr(803), new IntPtr(804), new IntPtr(801), new IntPtr(802) },
            new IntPtr(803),
            new[] { "tab-a", "tab-b", "tab-new", "tab-user" },
            "tab-new",
            targetWindowIsForeground: true);
        CheckTrue("two concurrent additions make ownership permanently unsafe",
            ambiguousDualIdentityPolicy.ObserveForBinding(
                twoNativeAdditions,
                twoNativeAdditions,
                out _) == ExplorerTabIdentityPolicyDecision.Unsafe
            && ambiguousDualIdentityPolicy.IsCancelled);

        ExplorerTabDualIdentityPolicy.TryCreate(
            coherentIdentityInitial,
            coherentIdentityInitial,
            out var reorderedDualIdentityPolicy);
        var reorderedCreatedIdentity = new ExplorerTabIdentityPolicySnapshot(
            new[] { new IntPtr(803), new IntPtr(801), new IntPtr(802) },
            new IntPtr(803),
            new[] { "tab-b", "tab-a", "tab-new" },
            "tab-new",
            targetWindowIsForeground: true);
        CheckTrue("retained UIA tab reordering is never guessed through",
            reorderedDualIdentityPolicy.ObserveForBinding(
                reorderedCreatedIdentity,
                reorderedCreatedIdentity,
                out _) == ExplorerTabIdentityPolicyDecision.Unsafe
            && reorderedDualIdentityPolicy.IsCancelled);

        ExplorerTabDualIdentityPolicy.TryCreate(
            coherentIdentityInitial,
            coherentIdentityInitial,
            out var thirdSelectionDualIdentityPolicy);
        var thirdSelection = new ExplorerTabIdentityPolicySnapshot(
            new[] { new IntPtr(803), new IntPtr(801), new IntPtr(802) },
            new IntPtr(801),
            new[] { "tab-a", "tab-b", "tab-new" },
            "tab-a",
            targetWindowIsForeground: true);
        CheckTrue("a third-tab selection permanently cancels identity binding",
            thirdSelectionDualIdentityPolicy.ObserveForBinding(
                thirdSelection,
                thirdSelection,
                out _) == ExplorerTabIdentityPolicyDecision.Unsafe
            && thirdSelectionDualIdentityPolicy.IsCancelled);

        var nativeInitial = new ExplorerNativeTabSnapshot(
            new IntPtr(900),
            processId: 42,
            processStartTimeUtcTicks: 123_456,
            new[] { new IntPtr(901), new IntPtr(902) },
            new IntPtr(902),
            lastInputTick: 777,
            targetWindowIsForeground: true);
        CheckTrue("native ownership starts only from two identical pre-command samples",
            ExplorerTabNativeOwnershipLease.TryCreate(
                nativeInitial,
                nativeInitial,
                out var nativeOwnership));
        CheckTrue("the tab command is authorized only while the exact preflight is current",
            nativeOwnership.CanPostCommand(nativeInitial)
            && nativeOwnership.InitialTabCount == 2);
        var stalePreCommand = new ExplorerNativeTabSnapshot(
            new IntPtr(900),
            processId: 42,
            processStartTimeUtcTicks: 123_456,
            new[] { new IntPtr(901), new IntPtr(902) },
            new IntPtr(902),
            lastInputTick: 778,
            targetWindowIsForeground: true);
        CheckFalse("input arriving after preflight prevents the private tab command",
            nativeOwnership.CanPostCommand(stalePreCommand));

        var nativeCreated = new ExplorerNativeTabSnapshot(
            new IntPtr(900),
            processId: 42,
            processStartTimeUtcTicks: 123_456,
            new[] { new IntPtr(903), new IntPtr(901), new IntPtr(902) },
            new IntPtr(903),
            lastInputTick: 777,
            targetWindowIsForeground: true);
        CheckTrue("one stable active native delta is causally claimed",
            nativeOwnership.TryClaimCreated(
                nativeCreated,
                nativeCreated,
                out var claimedNative) == ExplorerNativeTabClaimDecision.Claimed
            && claimedNative == new IntPtr(903)
            && nativeOwnership.ClaimedTabHandle == new IntPtr(903));
        CheckTrue("exact top-level and ShellBrowser handles authorize COM navigation",
            nativeOwnership.CanUseExactComItem(
                new IntPtr(900),
                new IntPtr(903),
                nativeCreated));
        CheckFalse("a mismatched ShellBrowser handle can never be navigated",
            nativeOwnership.CanUseExactComItem(
                new IntPtr(900),
                new IntPtr(904),
                nativeCreated));
        CheckTrue("a closed owned tab permits fallback only after the original topology is restored",
            nativeOwnership.CanOpenFallbackAfterOwnedTabClosed(nativeInitial));
        CheckFalse("input during owned-tab cleanup permanently suppresses fallback",
            nativeOwnership.CanOpenFallbackAfterOwnedTabClosed(
                new ExplorerNativeTabSnapshot(
                    new IntPtr(900),
                    processId: 42,
                    processStartTimeUtcTicks: 123_456,
                    new[] { new IntPtr(901), new IntPtr(902) },
                    new IntPtr(902),
                    lastInputTick: 778,
                    targetWindowIsForeground: true)));

        ExplorerTabNativeOwnershipLease.TryCreate(
            nativeInitial,
            nativeInitial,
            out var inputChangedOwnership);
        var inputChangedBeforeClaim = new ExplorerNativeTabSnapshot(
            new IntPtr(900),
            processId: 42,
            processStartTimeUtcTicks: 123_456,
            new[] { new IntPtr(903), new IntPtr(901), new IntPtr(902) },
            new IntPtr(903),
            lastInputTick: 778,
            targetWindowIsForeground: true);
        CheckTrue("physical input before native claim permanently refuses ownership",
            inputChangedOwnership.TryClaimCreated(
                inputChangedBeforeClaim,
                inputChangedBeforeClaim,
                out _) == ExplorerNativeTabClaimDecision.UserIntervened
            && inputChangedOwnership.ActivationRevoked);

        ExplorerTabNativeOwnershipLease.TryCreate(
            nativeInitial,
            nativeInitial,
            out var knownRepeatOwnership);
        CheckTrue("a confirmed duplicate folder request may finish the exact native claim",
            knownRepeatOwnership.TryClaimCreated(
                inputChangedBeforeClaim,
                inputChangedBeforeClaim,
                out var repeatedClickClaim,
                allowInputChangeFromKnownDuplicateRequest: true)
            == ExplorerNativeTabClaimDecision.Claimed
            && repeatedClickClaim == new IntPtr(903)
            && knownRepeatOwnership.ActivationRevoked
            && knownRepeatOwnership.CanUseExactComItem(
                new IntPtr(900),
                new IntPtr(903),
                inputChangedBeforeClaim));

        ExplorerTabNativeOwnershipLease.TryCreate(
            nativeInitial,
            nativeInitial,
            out var twoDeltaOwnership);
        var twoNativeDeltas = new ExplorerNativeTabSnapshot(
            new IntPtr(900),
            processId: 42,
            processStartTimeUtcTicks: 123_456,
            new[] { new IntPtr(903), new IntPtr(904), new IntPtr(901), new IntPtr(902) },
            new IntPtr(904),
            lastInputTick: 777,
            targetWindowIsForeground: true);
        CheckTrue("two native deltas are ambiguous and never guessed through",
            twoDeltaOwnership.TryClaimCreated(
                twoNativeDeltas,
                twoNativeDeltas,
                out _) == ExplorerNativeTabClaimDecision.Unsafe
            && twoDeltaOwnership.IsUnsafe);

        var focusLostAfterClaim = new ExplorerNativeTabSnapshot(
            new IntPtr(900),
            processId: 42,
            processStartTimeUtcTicks: 123_456,
            new[] { new IntPtr(903), new IntPtr(901), new IntPtr(902) },
            new IntPtr(903),
            lastInputTick: 778,
            targetWindowIsForeground: false);
        CheckFalse("later user input revokes auto-activation without erasing exact ownership",
            nativeOwnership.ObserveActivationIntent(
                focusLostAfterClaim,
                expectedActiveTabHandle: new IntPtr(903)));
        CheckTrue("exact claimed COM identity remains navigable after focus loss",
            nativeOwnership.CanUseExactComItem(
                new IntPtr(900),
                new IntPtr(903),
                focusLostAfterClaim));
        CheckFalse("user takeover forbids failed-operation cleanup",
            nativeOwnership.CanCloseExactComItem(
                new IntPtr(900),
                new IntPtr(903),
                focusLostAfterClaim,
                ExplorerNavigationDisposition.KnownRejected));
        CheckFalse("an accepted or unknown navigation never permits cleanup or fallback",
            nativeOwnership.CanCloseExactComItem(
                new IntPtr(900),
                new IntPtr(903),
                nativeCreated,
                ExplorerNavigationDisposition.Accepted)
            || ExplorerNavigationDispositionPolicy.ShouldOpenFallback(
                ExplorerNavigationDisposition.Unknown)
            || ExplorerNavigationDispositionPolicy.ShouldOpenFallback(
                ExplorerNavigationDisposition.Accepted));
        CheckTrue("a known pre-navigation rejection permits one native fallback",
            ExplorerNavigationDispositionPolicy.ShouldOpenFallback(
                ExplorerNavigationDisposition.KnownRejected));
        CheckTrue("an exact known rejection still permits fallback while ownership is intact",
            ExplorerNavigationDispositionPolicy.ShouldOpenFallback(
                ExplorerNavigationDisposition.KnownRejected,
                exactIdentityPreserved: true));
        CheckFalse("identity loss after a known rejection suppresses fallback",
            ExplorerNavigationDispositionPolicy.ShouldOpenFallback(
                ExplorerNavigationDisposition.KnownRejected,
                exactIdentityPreserved: false));
        CheckTrue("a confirmed ready target may release the visual mask immediately",
            ExplorerVisualNavigationPolicy.ShouldReleaseVisualMaskImmediately(
                ExplorerNavigationDisposition.Accepted,
                targetIsReady: true));
        CheckFalse("an accepted but not-ready target keeps the visual mask until its guard ends",
            ExplorerVisualNavigationPolicy.ShouldReleaseVisualMaskImmediately(
                ExplorerNavigationDisposition.Accepted,
                targetIsReady: false));
        CheckFalse("an unknown navigation outcome never releases the visual mask early",
            ExplorerVisualNavigationPolicy.ShouldReleaseVisualMaskImmediately(
                ExplorerNavigationDisposition.Unknown,
                targetIsReady: true));
        CheckTrue("default open experience uses the responsive native path without visual masking or readiness waits",
            ExplorerOpenExperiencePolicy.DefaultMode
            == ExplorerOpenPresentationMode.ResponsiveNative
            && ExplorerOpenExperiencePolicy.UsesDirectResponsivePipeline(
                ExplorerOpenExperiencePolicy.DefaultMode)
            && !ExplorerOpenExperiencePolicy.RequiresForegroundTarget(
                ExplorerOpenExperiencePolicy.DefaultMode)
            && !ExplorerOpenExperiencePolicy.UsesVisualMask(
                ExplorerOpenExperiencePolicy.DefaultMode)
            && !ExplorerOpenExperiencePolicy.WaitsForTargetReady(
                ExplorerOpenExperiencePolicy.DefaultMode)
            && !ExplorerOpenExperiencePolicy.ShouldPrewarmVisualMask(
                ExplorerOpenExperiencePolicy.DefaultMode)
            && !ExplorerOpenExperiencePolicy.RestoresOriginalDuringNavigation(
                ExplorerOpenExperiencePolicy.DefaultMode));
        CheckTrue("visual presentation modes retain their guarded foreground-only pipeline",
            !ExplorerOpenExperiencePolicy.UsesDirectResponsivePipeline(
                ExplorerOpenPresentationMode.MaskedUntilReady)
            && ExplorerOpenExperiencePolicy.RequiresForegroundTarget(
                ExplorerOpenPresentationMode.MaskedUntilReady));
        CheckTrue("known duplicate folder input remains valid in the foreground-loading experience",
            ExplorerOpenExperiencePolicy.AllowsKnownDuplicateRequestInput(
                ExplorerOpenPresentationMode.MaskedUntilReady));
        CheckTrue("an unavailable visual mask degrades to the native 0.2.4 behavior instead of losing the folder request",
            ExplorerOpenExperiencePolicy.ResolveAfterVisualMaskAttempt(
                ExplorerOpenPresentationMode.MaskedUntilReady,
                maskAvailable: false)
            == ExplorerOpenPresentationMode.ResponsiveNative);
        CheckTrue("a ready visual mask preserves the requested foreground-loading behavior",
            ExplorerOpenExperiencePolicy.ResolveAfterVisualMaskAttempt(
                ExplorerOpenPresentationMode.MaskedUntilReady,
                maskAvailable: true)
            == ExplorerOpenPresentationMode.MaskedUntilReady);
        CheckFalse("the responsive native experience never prewarms mask geometry",
            ExplorerOpenExperiencePolicy.ShouldPrewarmVisualMask(
                ExplorerOpenPresentationMode.ResponsiveNative));
        CheckTrue("the loading placeholder names a drive without exposing Explorer's default page",
            ExplorerVisualMaskPresentation.CreateLoadingMessage(@"E:\")
            == "正在打开 E 盘…");
        CheckTrue("background transition mask has a short non-renewable upper bound",
            ExplorerOpenExperiencePolicy.GetVisualMaskHardTimeoutMilliseconds(
                ExplorerOpenPresentationMode.BackgroundUntilReady) == 2_000);
        CheckFalse("transition mask stays when the exact native original tab is not active",
            ExplorerOpenExperiencePolicy.CanReleaseBackgroundTransitionMask(
                originalNativeTabIsActive: false,
                stableMilliseconds: ExplorerOpenExperiencePolicy.BackgroundRestoreStabilityMilliseconds));
        CheckFalse("transition mask stays until the restored original has remained stable",
            ExplorerOpenExperiencePolicy.CanReleaseBackgroundTransitionMask(
                originalNativeTabIsActive: true,
                stableMilliseconds: ExplorerOpenExperiencePolicy.BackgroundRestoreStabilityMilliseconds - 1));
        CheckTrue("transition mask releases after the exact native original is stable without a synchronous UIA read",
            ExplorerOpenExperiencePolicy.CanReleaseBackgroundTransitionMask(
                originalNativeTabIsActive: true,
                stableMilliseconds: ExplorerOpenExperiencePolicy.BackgroundRestoreStabilityMilliseconds));
        CheckTrue("an appended ninth tab can use Explorer's verified ordinal fast path",
            ExplorerOpenExperiencePolicy.CanSelectAppendedNewTabWithoutUia(
                initialTabCount: 8));
        CheckTrue("an appended tenth tab uses the current Explorer build's verified ordinal fast path",
            ExplorerOpenExperiencePolicy.CanSelectAppendedNewTabWithoutUia(
                initialTabCount: 9));
        CheckTrue("an appended eleventh tab uses the current Explorer build's verified ordinal fast path",
            ExplorerOpenExperiencePolicy.CanSelectAppendedNewTabWithoutUia(
                initialTabCount: 10));
        CheckTrue("an appended twelfth tab uses the current Explorer build's verified ordinal fast path",
            ExplorerOpenExperiencePolicy.CanSelectAppendedNewTabWithoutUia(
                initialTabCount: 11));
        CheckTrue("the captured tenth original tab can use the verified native restore path",
            ExplorerOpenExperiencePolicy.CanSelectPrivateOrdinalWithoutUia(
                oneBasedOrdinal: 10));
        CheckTrue("the captured eleventh original tab can use the verified native restore path",
            ExplorerOpenExperiencePolicy.CanSelectPrivateOrdinalWithoutUia(
                oneBasedOrdinal: 11));
        CheckTrue("the captured twelfth original tab can use the verified native restore path",
            ExplorerOpenExperiencePolicy.CanSelectPrivateOrdinalWithoutUia(
                oneBasedOrdinal: 12));
        CheckTrue("an appended thirteenth tab uses Explorer's range-checked ordinal path",
            ExplorerOpenExperiencePolicy.CanSelectAppendedNewTabWithoutUia(
                initialTabCount: 12));
        CheckTrue("the captured thirteenth original tab uses Explorer's range-checked restore path",
            ExplorerOpenExperiencePolicy.CanSelectPrivateOrdinalWithoutUia(
                oneBasedOrdinal: 13));
        CheckTrue("a verified twentieth ordinal remains on the native selection path",
            ExplorerOpenExperiencePolicy.CanSelectPrivateOrdinalWithoutUia(
                oneBasedOrdinal: 20));
        CheckTrue("a verified thirty-sixth appended tab remains on the native selection path",
            ExplorerOpenExperiencePolicy.CanSelectAppendedNewTabWithoutUia(
                initialTabCount: 35));
        CheckFalse("ordinal selection rejects the non-tab zero ordinal",
            ExplorerOpenExperiencePolicy.CanSelectPrivateOrdinalWithoutUia(
                oneBasedOrdinal: 0));

        var operationLifetime = new ExplorerOperationLifetime(initiallyAccepting: true);
        CheckTrue("an enabled operation lifetime admits a current request",
            operationLifetime.TryBegin(out var firstOperation)
            && firstOperation != null
            && operationLifetime.IsCurrent(firstOperation));
        CheckFalse("retiring with an in-flight request defers shared COM cleanup",
            operationLifetime.Retire());
        CheckFalse("a retired lifetime rejects new Explorer requests",
            operationLifetime.TryBegin(out _));
        CheckFalse("retirement permanently revokes the old request generation",
            operationLifetime.IsCurrent(firstOperation));
        CheckTrue("the final retired request authorizes deferred COM cleanup exactly once",
            operationLifetime.Complete(firstOperation));
        CheckFalse("completing the same request twice cannot release shared state twice",
            operationLifetime.Complete(firstOperation));
        operationLifetime.Activate();
        CheckTrue("reactivation admits only a fresh request generation",
            operationLifetime.TryBegin(out var secondOperation)
            && secondOperation != null
            && operationLifetime.IsCurrent(secondOperation)
            && !operationLifetime.IsCurrent(firstOperation));
        CheckFalse("an active generation completion does not request COM retirement",
            operationLifetime.Complete(secondOperation));
        CheckTrue("retiring an idle lifetime allows immediate COM cleanup",
            operationLifetime.Retire());

        var activationLease = new ExplorerTabActivationLease(
            new IntPtr(700),
            new IntPtr(701),
            new[] { "tab-a", "tab-b", "tab-c" },
            "tab-b");
        CheckFalse("tab identity binding waits for the native created tab to be active",
            activationLease.TryBindCreatedTab(
                new IntPtr(704),
                new TabStripObservation(
                    new IntPtr(701),
                    new[] { "tab-a", "tab-b", "tab-c", "tab-new" },
                    "tab-new",
                    targetWindowIsForeground: true)));
        CheckTrue("created HWND binds to the one uniquely added selected UIA tab",
            activationLease.TryBindCreatedTab(
                new IntPtr(704),
                new TabStripObservation(
                    new IntPtr(704),
                    new[] { "tab-a", "tab-b", "tab-c", "tab-new" },
                    "tab-new",
                    targetWindowIsForeground: true))
            && activationLease.CreatedTabHandle == new IntPtr(704)
            && activationLease.CreatedRuntimeId == "tab-new");
        CheckTrue("original restore is authorized only while the bound new tab is selected",
            activationLease.TryAuthorizeOriginalRestore(
                new TabStripObservation(
                    new IntPtr(704),
                    new[] { "tab-a", "tab-b", "tab-c", "tab-new" },
                    "tab-new",
                    targetWindowIsForeground: true)));
        CheckFalse("native and UIA restore observations may settle on adjacent samples",
            activationLease.ObserveOriginalRestore(
                new TabStripObservation(
                    new IntPtr(701),
                    new[] { "tab-a", "tab-b", "tab-c", "tab-new" },
                    "tab-new",
                    targetWindowIsForeground: true)));
        CheckFalse("restore telemetry skew does not masquerade as user intervention",
            activationLease.IsActivationCancelled);
        CheckTrue("lease records the exact original tab restoration",
            activationLease.ObserveOriginalRestore(
                new TabStripObservation(
                    new IntPtr(701),
                    new[] { "tab-a", "tab-b", "tab-c", "tab-new" },
                    "tab-b",
                    targetWindowIsForeground: true)));
        CheckTrue("unchanged user intent permits final activation",
            activationLease.CanActivateCreatedTab(
                new TabStripObservation(
                    new IntPtr(701),
                    new[] { "tab-a", "tab-b", "tab-c", "tab-new" },
                    "tab-b",
                    targetWindowIsForeground: true)));

        var postRestoreBindingLease = new ExplorerTabActivationLease(
            new IntPtr(705),
            new IntPtr(706),
            new[] { "tab-a", "tab-b", "tab-c" },
            "tab-b");
        CheckTrue("a uniquely added UIA tab can bind after the exact native original is restored",
            postRestoreBindingLease.TryBindCreatedTabAfterOriginalRestore(
                new IntPtr(709),
                new TabStripObservation(
                    new IntPtr(706),
                    new[] { "tab-a", "tab-b", "tab-c", "tab-new" },
                    "tab-b",
                    targetWindowIsForeground: true)));
        CheckTrue("post-restore binding records both created identities",
            postRestoreBindingLease.CreatedTabHandle == new IntPtr(709)
            && postRestoreBindingLease.CreatedRuntimeId == "tab-new"
            && postRestoreBindingLease.CanActivateCreatedTab(
                new TabStripObservation(
                    new IntPtr(706),
                    new[] { "tab-a", "tab-b", "tab-c", "tab-new" },
                    "tab-b",
                    targetWindowIsForeground: true)));

        var ambiguousIdentityLease = new ExplorerTabActivationLease(
            new IntPtr(710),
            new IntPtr(711),
            new[] { "tab-a", "tab-b" },
            "tab-a");
        CheckFalse("two added UIA tabs can never bind to one created HWND",
            ambiguousIdentityLease.TryBindCreatedTab(
                new IntPtr(713),
                new TabStripObservation(
                    new IntPtr(713),
                    new[] { "tab-a", "tab-b", "tab-new", "tab-other" },
                    "tab-new",
                    targetWindowIsForeground: true)));
        CheckTrue("ambiguous UIA identity permanently cancels automatic activation",
            ambiguousIdentityLease.IsActivationCancelled);

        var reorderedBeforeRestoreLease = new ExplorerTabActivationLease(
            new IntPtr(720),
            new IntPtr(721),
            new[] { "tab-a", "tab-b", "tab-c" },
            "tab-b");
        CheckTrue("reorder test binds an otherwise valid created tab",
            reorderedBeforeRestoreLease.TryBindCreatedTab(
                new IntPtr(724),
                new TabStripObservation(
                    new IntPtr(724),
                    new[] { "tab-a", "tab-b", "tab-c", "tab-new" },
                    "tab-new",
                    targetWindowIsForeground: true)));
        CheckFalse("reordering before restore refuses the stale original ordinal",
            reorderedBeforeRestoreLease.TryAuthorizeOriginalRestore(
                new TabStripObservation(
                    new IntPtr(724),
                    new[] { "tab-b", "tab-a", "tab-c", "tab-new" },
                    "tab-new",
                    targetWindowIsForeground: true)));
        CheckTrue("a pre-restore reorder permanently cancels automatic activation",
            reorderedBeforeRestoreLease.IsActivationCancelled);

        var closedBeforeRestoreLease = new ExplorerTabActivationLease(
            new IntPtr(725),
            new IntPtr(726),
            new[] { "tab-a", "tab-b", "tab-c" },
            "tab-b");
        CheckTrue("close-before-restore test binds an otherwise valid created tab",
            closedBeforeRestoreLease.TryBindCreatedTab(
                new IntPtr(729),
                new TabStripObservation(
                    new IntPtr(729),
                    new[] { "tab-a", "tab-b", "tab-c", "tab-new" },
                    "tab-new",
                    targetWindowIsForeground: true)));
        CheckFalse("closing a tab before restore refuses the stale original ordinal",
            closedBeforeRestoreLease.TryAuthorizeOriginalRestore(
                new TabStripObservation(
                    new IntPtr(729),
                    new[] { "tab-a", "tab-b", "tab-new" },
                    "tab-new",
                    targetWindowIsForeground: true)));
        CheckTrue("a pre-restore close permanently cancels automatic activation",
            closedBeforeRestoreLease.IsActivationCancelled);

        var switchedAwayLease = CreateRestoredActivationLease(730);
        switchedAwayLease.ObserveDuringNavigation(
            new TabStripObservation(
                new IntPtr(732),
                new[] { "tab-a", "tab-b", "tab-new" },
                "tab-b",
                targetWindowIsForeground: true));
        switchedAwayLease.ObserveDuringNavigation(
            new TabStripObservation(
                new IntPtr(731),
                new[] { "tab-a", "tab-b", "tab-new" },
                "tab-a",
                targetWindowIsForeground: true));
        CheckTrue("switching away once permanently cancels delayed auto-activation",
            switchedAwayLease.IsActivationCancelled);
        CheckFalse("switching back cannot resurrect a cancelled activation lease",
            switchedAwayLease.CanActivateCreatedTab(
                new TabStripObservation(
                    new IntPtr(731),
                    new[] { "tab-a", "tab-b", "tab-new" },
                    "tab-a",
                    targetWindowIsForeground: true)));

        var closedDuringWaitLease = CreateRestoredActivationLease(740);
        closedDuringWaitLease.ObserveDuringNavigation(
            new TabStripObservation(
                new IntPtr(741),
                new[] { "tab-a", "tab-new" },
                "tab-a",
                targetWindowIsForeground: true));
        CheckTrue("closing any leased tab during navigation cancels auto-activation",
            closedDuringWaitLease.IsActivationCancelled);

        var reorderedDuringWaitLease = CreateRestoredActivationLease(750);
        reorderedDuringWaitLease.ObserveDuringNavigation(
            new TabStripObservation(
                new IntPtr(751),
                new[] { "tab-b", "tab-a", "tab-new" },
                "tab-a",
                targetWindowIsForeground: true));
        CheckTrue("reordering during navigation cancels auto-activation",
            reorderedDuringWaitLease.IsActivationCancelled);

        var foregroundChangedLease = CreateRestoredActivationLease(755);
        foregroundChangedLease.ObserveDuringNavigation(
            new TabStripObservation(
                new IntPtr(756),
                new[] { "tab-a", "tab-b", "tab-new" },
                "tab-a",
                targetWindowIsForeground: false));
        CheckTrue("leaving the target window permanently cancels delayed auto-activation",
            foregroundChangedLease.IsActivationCancelled);
        CheckFalse("returning to the target window does not revive cancelled activation",
            foregroundChangedLease.CanActivateCreatedTab(
                new TabStripObservation(
                    new IntPtr(756),
                    new[] { "tab-a", "tab-b", "tab-new" },
                    "tab-a",
                    targetWindowIsForeground: true)));

        var committedLease = CreateRestoredActivationLease(760);
        committedLease.MarkNavigationCommitted();
        CheckFalse("a committed navigation never opens a duplicate native fallback",
            committedLease.ShouldOpenNativeFallback);
        CheckFalse("a committed navigation tab is never removed as failed cleanup",
            committedLease.CanCloseCreatedTab(
                new IntPtr(760),
                new IntPtr(763),
                new[] { new IntPtr(761), new IntPtr(763) },
                new[] { "tab-a", "tab-b", "tab-new" }));

        var cleanupLease = CreateRestoredActivationLease(770);
        CheckFalse("cleanup never closes a COM item from another Explorer window",
            cleanupLease.CanCloseCreatedTab(
                new IntPtr(999),
                new IntPtr(773),
                new[] { new IntPtr(771), new IntPtr(773) },
                new[] { "tab-a", "tab-b", "tab-new" }));
        CheckFalse("cleanup never closes a tab whose HWND is not the created tab",
            cleanupLease.CanCloseCreatedTab(
                new IntPtr(770),
                new IntPtr(772),
                new[] { new IntPtr(771), new IntPtr(772), new IntPtr(773) },
                new[] { "tab-a", "tab-b", "tab-new" }));
        CheckFalse("cleanup never closes the last Explorer tab",
            cleanupLease.CanCloseCreatedTab(
                new IntPtr(770),
                new IntPtr(773),
                new[] { new IntPtr(773) },
                new[] { "tab-new" }));
        CheckTrue("cleanup may close only the exact still-owned uncommitted tab",
            cleanupLease.CanCloseCreatedTab(
                new IntPtr(770),
                new IntPtr(773),
                new[] { new IntPtr(771), new IntPtr(773) },
                new[] { "tab-a", "tab-b", "tab-new" }));

        var registrationTimeoutResult = OpenTabResult.Failed(
            OpenTabResultKind.ShellRegistrationTimedOut);
        CheckTrue("open-tab result exposes a stable shell-registration failure",
            !registrationTimeoutResult.IsSuccess
            && registrationTimeoutResult.ShouldOpenFallback
            && registrationTimeoutResult.FailureCode == "shell-registration-timeout"
            && registrationTimeoutResult.HResult == null);

        var visualMaskUnavailableResult = OpenTabResult.Failed(
            OpenTabResultKind.VisualMaskUnavailable);
        CheckTrue("zero-flicker preflight falls back before creating a visibly blank tab",
            !visualMaskUnavailableResult.IsSuccess
            && visualMaskUnavailableResult.ShouldOpenFallback
            && visualMaskUnavailableResult.FailureCode == "visual-mask-unavailable");

        var disconnectedResult = OpenTabResult.Failed(
            OpenTabResultKind.ShellDisconnected,
            unchecked((int)0x80010108));
        CheckTrue("open-tab result retains a COM HRESULT without leaking arbitrary text",
            disconnectedResult.FailureCode == "shell-disconnected"
            && disconnectedResult.HResult == unchecked((int)0x80010108));

        CheckTrue("COM policy classifies call rejection as transient busy",
            ExplorerComPolicy.Classify(unchecked((int)0x80010001)) == ExplorerComFailureKind.Busy
            && ExplorerComPolicy.Classify(unchecked((int)0x8001010A)) == ExplorerComFailureKind.Busy);
        CheckTrue("COM policy classifies dead Explorer connections as permanent disconnects",
            ExplorerComPolicy.Classify(unchecked((int)0x80010108)) == ExplorerComFailureKind.Disconnected
            && ExplorerComPolicy.Classify(unchecked((int)0x800401FD)) == ExplorerComFailureKind.Disconnected
            && ExplorerComPolicy.Classify(unchecked((int)0x80010007)) == ExplorerComFailureKind.Disconnected
            && ExplorerComPolicy.Classify(unchecked((int)0x800706BA)) == ExplorerComFailureKind.Disconnected);
        CheckTrue("COM policy leaves unrelated HRESULTs untouched",
            ExplorerComPolicy.Classify(unchecked((int)0x80004005)) == ExplorerComFailureKind.Other);
        CheckTrue("busy retry delays are short and strictly bounded",
            ExplorerComPolicy.GetRetryDelayMilliseconds(0) == 25
            && ExplorerComPolicy.GetRetryDelayMilliseconds(1) == 75
            && ExplorerComPolicy.GetRetryDelayMilliseconds(2) == 150
            && ExplorerComPolicy.GetRetryDelayMilliseconds(3) == 0);

        var busyThenAcceptedCalls = 0;
        var busyThenAcceptedValidations = 0;
        var busyThenAccepted = ExplorerNavigationSubmission.SubmitAsync(
                () =>
                {
                    busyThenAcceptedCalls++;
                    if (busyThenAcceptedCalls < 3)
                        throw new System.Runtime.InteropServices.COMException(
                            "Injected Explorer busy response.",
                            unchecked((int)0x80010001));
                },
                new RequestTimeBudget(TimeSpan.FromSeconds(1)),
                () =>
                {
                    busyThenAcceptedValidations++;
                    return true;
                })
            .GetAwaiter()
            .GetResult();
        CheckTrue("navigation retries only transient busy responses while exact identity survives",
            busyThenAccepted.Disposition == ExplorerNavigationDisposition.Accepted
            && busyThenAccepted.FailureKind == null
            && busyThenAccepted.ExactIdentityPreserved
            && busyThenAcceptedCalls == 3
            && busyThenAcceptedValidations == 2);

        var alwaysBusyCalls = 0;
        var alwaysBusyValidations = 0;
        var alwaysBusy = ExplorerNavigationSubmission.SubmitAsync(
                () =>
                {
                    alwaysBusyCalls++;
                    throw new System.Runtime.InteropServices.COMException(
                        "Injected Explorer busy response.",
                        unchecked((int)0x8001010A));
                },
                new RequestTimeBudget(TimeSpan.FromSeconds(1)),
                () =>
                {
                    alwaysBusyValidations++;
                    return true;
                })
            .GetAwaiter()
            .GetResult();
        CheckTrue("navigation stops after the bounded busy retry schedule",
            alwaysBusy.Disposition == ExplorerNavigationDisposition.KnownRejected
            && alwaysBusy.FailureKind == ExplorerComFailureKind.Busy
            && alwaysBusy.ExactIdentityPreserved
            && alwaysBusyCalls == 4
            && alwaysBusyValidations == 3);

        var rejectedAfterIdentityLossCalls = 0;
        var rejectedAfterIdentityLoss = ExplorerNavigationSubmission.SubmitAsync(
                () =>
                {
                    rejectedAfterIdentityLossCalls++;
                    throw new System.Runtime.InteropServices.COMException(
                        "Injected Explorer busy response.",
                        unchecked((int)0x80010001));
                },
                new RequestTimeBudget(TimeSpan.FromSeconds(1)),
                () => false)
            .GetAwaiter()
            .GetResult();
        CheckTrue("a busy retry is cancelled when exact tab ownership is lost",
            rejectedAfterIdentityLoss.Disposition == ExplorerNavigationDisposition.KnownRejected
            && !rejectedAfterIdentityLoss.ExactIdentityPreserved
            && rejectedAfterIdentityLossCalls == 1
            && !ExplorerNavigationDispositionPolicy.ShouldOpenFallback(
                rejectedAfterIdentityLoss.Disposition,
                rejectedAfterIdentityLoss.ExactIdentityPreserved));

        var disconnectedCalls = 0;
        var disconnectedSubmission = ExplorerNavigationSubmission.SubmitAsync(
                () =>
                {
                    disconnectedCalls++;
                    throw new System.Runtime.InteropServices.COMException(
                        "Injected Explorer disconnect.",
                        unchecked((int)0x80010108));
                },
                new RequestTimeBudget(TimeSpan.FromSeconds(1)),
                () => throw new InvalidOperationException("A non-busy call must not retry."))
            .GetAwaiter()
            .GetResult();
        CheckTrue("a disconnect after submission is unknown and never opens a duplicate fallback",
            disconnectedSubmission.Disposition == ExplorerNavigationDisposition.Unknown
            && disconnectedSubmission.FailureKind == ExplorerComFailureKind.Disconnected
            && disconnectedSubmission.HResult == unchecked((int)0x80010108)
            && disconnectedCalls == 1
            && !ExplorerNavigationDispositionPolicy.ShouldOpenFallback(
                disconnectedSubmission.Disposition));

        var unknownCalls = 0;
        var unknownSubmission = ExplorerNavigationSubmission.SubmitAsync(
                () =>
                {
                    unknownCalls++;
                    throw new InvalidOperationException("Injected ambiguous submission failure.");
                },
                new RequestTimeBudget(TimeSpan.FromSeconds(1)),
                () => throw new InvalidOperationException("An unknown call must not retry."))
            .GetAwaiter()
            .GetResult();
        CheckTrue("a non-COM submission failure remains unknown and suppresses fallback",
            unknownSubmission.Disposition == ExplorerNavigationDisposition.Unknown
            && unknownSubmission.HResult == null
            && unknownSubmission.FailureKind == null
            && unknownCalls == 1
            && !ExplorerNavigationDispositionPolicy.ShouldOpenFallback(
                unknownSubmission.Disposition));

        Check("registration timeout starts with a cold-safe budget", "3000",
            ShellRegistrationTimeoutPolicy.CalculateMaximumMilliseconds(Array.Empty<int>()).ToString());
        Check("registration timeout contracts after consistently fast registration", "2000",
            ShellRegistrationTimeoutPolicy.CalculateMaximumMilliseconds(new[] { 350, 420, 600 }).ToString());
        Check("registration timeout expands when Explorer has recently been slow", "3400",
            ShellRegistrationTimeoutPolicy.CalculateMaximumMilliseconds(new[] { 500, 700, 1700 }).ToString());
        Check("registration timeout remains capped", "4000",
            ShellRegistrationTimeoutPolicy.CalculateMaximumMilliseconds(new[] { 5000 }).ToString());
        Check("a restored background tab keeps an eight-second registration opportunity", "8000",
            ShellRegistrationTimeoutPolicy.CalculateMaximumMilliseconds(
                Array.Empty<int>(),
                backgroundNavigation: true).ToString());

        var disabledTray = TrayStatusPresentation.Create(
            directOpenEnabled: false,
            ExplorerConnectionState.Ready);
        CheckTrue("tray reports disabled whenever direct Folder-open is off",
            disabledTray.State == QingTabOperationalState.Disabled
            && disabledTray.StatusText.Contains("已关闭"));

        var connectingTray = TrayStatusPresentation.Create(
            directOpenEnabled: true,
            ExplorerConnectionState.Connecting);
        CheckTrue("tray never claims ready while Explorer is still connecting",
            connectingTray.State == QingTabOperationalState.Connecting
            && connectingTray.StatusText.Contains("正在连接")
            && !connectingTray.StatusText.Contains("普通文件夹 → 新标签"));

        var readyTray = TrayStatusPresentation.Create(
            directOpenEnabled: true,
            ExplorerConnectionState.Ready);
        CheckTrue("tray reports ready only after the Explorer bridge is ready",
            readyTray.State == QingTabOperationalState.Ready
            && readyTray.StatusText.Contains("普通文件夹 → 新标签")
            && !readyTray.StatusText.Contains("零闪烁"));

        var trayMenu = TrayMenuPresentation.Create();
        CheckTrue("tray menu uses concise labels and offers one complete exit action",
            trayMenu.DirectOpenItemText == "普通打开文件夹 → 新标签"
            && !trayMenu.DirectOpenItemText.Contains("零闪烁")
            && trayMenu.ExitItemTexts.Count == 1
            && trayMenu.ExitItemTexts[0] == "退出");

        var successfulRestoreCalls = 0;
        var preparedExit = ApplicationExitPolicy.Prepare(
            delegate(out string error)
            {
                successfulRestoreCalls++;
                error = string.Empty;
                return true;
            });
        CheckTrue("tray exit restores Windows Folder-open behavior before allowing the resident to stop",
            preparedExit.CanExit
            && preparedExit.WindowsFolderOpenRestored
            && successfulRestoreCalls == 1);

        var blockedExit = ApplicationExitPolicy.Prepare(
            delegate(out string error)
            {
                error = "restore failed";
                return false;
            });
        CheckFalse("tray exit remains active when Windows Folder-open behavior cannot be restored safely",
            blockedExit.CanExit
            || blockedExit.WindowsFolderOpenRestored
            || blockedExit.Error != "restore failed");

        var reconnectingTray = TrayStatusPresentation.Create(
            directOpenEnabled: true,
            ExplorerConnectionState.Reconnecting);
        CheckTrue("tray distinguishes Explorer restart recovery from readiness",
            reconnectingTray.State == QingTabOperationalState.Reconnecting
            && reconnectingTray.StatusText.Contains("重新连接"));

        var unavailableTray = TrayStatusPresentation.Create(
            directOpenEnabled: true,
            ExplorerConnectionState.Unavailable);
        CheckTrue("tray exposes a temporarily unavailable bridge",
            unavailableTray.State == QingTabOperationalState.Unavailable
            && unavailableTray.StatusText.Contains("暂不可用"));

        var receivedAt = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);
        Check("default duplicate cooldown only filters one short Shell input burst", "300",
            OpenTabRequestQueue.DefaultDuplicateWindow.TotalMilliseconds.ToString("0"));
        var requestQueue = new OpenTabRequestQueue(
            capacity: 3,
            duplicateWindow: TimeSpan.FromMilliseconds(300));
        var firstRequest = new OpenTabRequest(@"C:\Work\Folder", new IntPtr(501), receivedAt);
        var duplicateRequest = new OpenTabRequest(@"c:\work\folder\", new IntPtr(501), receivedAt.AddMilliseconds(100));
        var otherWindowRequest = new OpenTabRequest(@"C:\Work\Folder", new IntPtr(502), receivedAt.AddMilliseconds(150));
        var laterRequest = new OpenTabRequest(@"C:\Work\Folder", new IntPtr(501), receivedAt.AddMilliseconds(350));

        CheckTrue("first request enters the queue", requestQueue.Enqueue(firstRequest) == OpenTabEnqueueResult.Accepted);
        CheckTrue("rapid duplicate is ignored", requestQueue.Enqueue(duplicateRequest) == OpenTabEnqueueResult.Duplicate);
        CheckTrue("same path for another Explorer window is preserved", requestQueue.Enqueue(otherWindowRequest) == OpenTabEnqueueResult.Accepted);
        CheckTrue("a deliberate repeat after the short duplicate window is preserved",
            requestQueue.Enqueue(laterRequest) == OpenTabEnqueueResult.Accepted);
        CheckTrue("bounded queue rejects overflow", requestQueue.Enqueue(
            new OpenTabRequest(@"D:\Overflow", new IntPtr(501), receivedAt.AddSeconds(1))) == OpenTabEnqueueResult.Full);

        CheckTrue("queue returns requests in arrival order",
            requestQueue.TryDequeue(out var dequeuedFirst)
            && ReferenceEquals(firstRequest, dequeuedFirst)
            && requestQueue.TryDequeue(out var dequeuedSecond)
            && ReferenceEquals(otherWindowRequest, dequeuedSecond)
            && requestQueue.TryDequeue(out var dequeuedThird)
            && ReferenceEquals(laterRequest, dequeuedThird));
        CheckTrue("queue is empty after draining", requestQueue.Count == 0);

        var inFlightQueue = new OpenTabRequestQueue(
            capacity: 4,
            duplicateWindow: TimeSpan.FromMilliseconds(300));
        var inFlightRequest = new OpenTabRequest(
            @"E:\\",
            new IntPtr(57104),
            receivedAt);
        CheckTrue("in-flight test accepts the first request",
            inFlightQueue.Enqueue(inFlightRequest) == OpenTabEnqueueResult.Accepted);
        CheckTrue("in-flight test starts the accepted request",
            inFlightQueue.TryDequeue(out var startedRequest)
            && ReferenceEquals(inFlightRequest, startedRequest));
        CheckTrue("a rapid duplicate still collapses into the in-flight request",
            inFlightQueue.Enqueue(new OpenTabRequest(
                @"e:\\",
                new IntPtr(57104),
                receivedAt.AddMilliseconds(100))) == OpenTabEnqueueResult.Duplicate);
        var deliberateSecondRequest = new OpenTabRequest(
            @"E:\\",
            new IntPtr(57104),
            receivedAt.AddMilliseconds(400));
        CheckTrue("a deliberate repeat is queued even while the first request is still in flight",
            inFlightQueue.Enqueue(deliberateSecondRequest) == OpenTabEnqueueResult.Accepted
            && inFlightQueue.Count == 1);
        CheckTrue("a rapid duplicate after the deliberate repeat is still collapsed",
            inFlightQueue.Enqueue(new OpenTabRequest(
                @"e:\\",
                new IntPtr(57104),
                receivedAt.AddMilliseconds(500))) == OpenTabEnqueueResult.Duplicate);
        var deliberateThirdRequest = new OpenTabRequest(
            @"E:\\",
            new IntPtr(57104),
            receivedAt.AddMilliseconds(750));
        CheckTrue("another deliberate repeat after the short cooldown is preserved",
            inFlightQueue.Enqueue(deliberateThirdRequest) == OpenTabEnqueueResult.Accepted);
        var deliberateFourthRequest = new OpenTabRequest(
            @"E:\\",
            new IntPtr(57104),
            receivedAt.AddMilliseconds(1_100));
        CheckTrue("each later deliberate click gets its own short cooldown",
            inFlightQueue.Enqueue(deliberateFourthRequest) == OpenTabEnqueueResult.Accepted);
        CheckTrue("preserved deliberate repeats keep FIFO order",
            inFlightQueue.TryDequeue(out var deliberateDequeuedSecond)
            && ReferenceEquals(deliberateSecondRequest, deliberateDequeuedSecond)
            && inFlightQueue.TryDequeue(out var deliberateDequeuedThird)
            && ReferenceEquals(deliberateThirdRequest, deliberateDequeuedThird)
            && inFlightQueue.TryDequeue(out var deliberateDequeuedFourth)
            && ReferenceEquals(deliberateFourthRequest, deliberateDequeuedFourth));

        var backpressureQueue = new OpenTabRequestQueue(capacity: 1);
        var blockingFirst = new OpenTabRequest(@"C:\First", new IntPtr(601), receivedAt);
        var blockingSecond = new OpenTabRequest(@"C:\Second", new IntPtr(601), receivedAt.AddSeconds(1));
        CheckTrue("backpressure test fills the bounded queue",
            backpressureQueue.Enqueue(blockingFirst) == OpenTabEnqueueResult.Accepted);
        var waitingEnqueue = Task.Run(() => backpressureQueue.Enqueue(blockingSecond, waitMilliseconds: 1_000));
        Thread.Sleep(30);
        CheckFalse("a full queue applies backpressure instead of bypassing FIFO", waitingEnqueue.IsCompleted);
        CheckTrue("dequeue releases one bounded queue slot",
            backpressureQueue.TryDequeue(out var blockingDequeued)
            && ReferenceEquals(blockingFirst, blockingDequeued));
        CheckTrue("the waiting request enters after the older request",
            waitingEnqueue.Result == OpenTabEnqueueResult.Accepted
            && backpressureQueue.TryDequeue(out var admittedAfterWait)
            && ReferenceEquals(blockingSecond, admittedAfterWait));

        Check("time budget is intact before work starts", "15000",
            RequestTimeBudget.CalculateRemainingMilliseconds(
                TimeSpan.FromSeconds(15), TimeSpan.Zero).ToString());
        Check("time budget carries queue time into the open operation", "5000",
            RequestTimeBudget.CalculateRemainingMilliseconds(
                TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(10)).ToString());
        Check("time budget expires at the total deadline", "0",
            RequestTimeBudget.CalculateRemainingMilliseconds(
                TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15)).ToString());
        Check("time budget never becomes negative", "0",
            RequestTimeBudget.CalculateRemainingMilliseconds(
                TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(1)).ToString());

        var logTestDirectory = Path.Combine(Path.GetTempPath(), "QingTab.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var logPath = Path.Combine(logTestDirectory, "QingTab-error.log");
            var rotatingLog = new RotatingTextLog(logPath, maximumBytes: 80, archiveCount: 2);
            rotatingLog.Append(new string('A', 100));
            rotatingLog.Append(new string('B', 100));
            rotatingLog.Append(new string('C', 100));
            rotatingLog.Append(new string('D', 100));

            CheckTrue("rotating log keeps the active file", File.ReadAllText(logPath).Contains("DDDD"));
            CheckTrue("rotating log keeps the newest archive", File.ReadAllText(logPath + ".1").Contains("CCCC"));
            CheckTrue("rotating log keeps the configured oldest archive", File.ReadAllText(logPath + ".2").Contains("BBBB"));
            CheckFalse("rotating log does not exceed archive count", File.Exists(logPath + ".3"));
            CheckTrue("active log remains within its byte limit", new FileInfo(logPath).Length <= 80);
            CheckTrue("every log archive remains within its byte limit",
                new FileInfo(logPath + ".1").Length <= 80
                && new FileInfo(logPath + ".2").Length <= 80);

            var privateError = new InvalidOperationException(
                @"Cannot open C:\Users\Example User\Secret Project\document.txt");
            var safeEntry = ErrorLog.FormatEntry(
                privateError,
                new DateTimeOffset(2026, 8, 8, 12, 34, 56, TimeSpan.FromHours(8)),
                "open-tab-failed");
            CheckTrue("structured error log retains a stable error code",
                safeEntry.Contains("Code=open-tab-failed")
                && safeEntry.Contains("System.InvalidOperationException"));
            CheckFalse("persistent error log omits exception messages and paths",
                safeEntry.Contains("Example User")
                || safeEntry.Contains("Secret Project")
                || safeEntry.Contains("document.txt"));
        }
        finally
        {
            if (Directory.Exists(logTestDirectory))
                Directory.Delete(logTestDirectory, recursive: true);
        }

        var names = InstanceObjectNames.Create("S-1-5-21-123", sessionId: 7);
        var otherSessionNames = InstanceObjectNames.Create("S-1-5-21-123", sessionId: 8);
        var otherUserNames = InstanceObjectNames.Create("S-1-5-21-456", sessionId: 7);
        Check("mutex name is scoped to user and session",
            @"Local\QingTab.SingleInstance.S_1_5_21_123.7",
            names.MutexName);
        Check("exit event name is scoped to user and session",
            @"Local\QingTab.ExitRequested.S_1_5_21_123.7",
            names.ExitEventName);
        Check("ready event name is scoped to user and session",
            @"Local\QingTab.Ready.S_1_5_21_123.7",
            names.ReadyEventName);
        Check("pipe name is scoped to user and session",
            "QingTab.OpenTab.S_1_5_21_123.7",
            names.PipeName);
        CheckTrue("another session receives isolated object names", names.MutexName != otherSessionNames.MutexName);
        CheckTrue("another user receives isolated object names", names.MutexName != otherUserNames.MutexName);

        var readyWithArbitraryText = new ExplorerConnectionStatus(
            ExplorerConnectionState.Ready,
            "localized text can change freely");
        var connectingWithReadyWords = new ExplorerConnectionStatus(
            ExplorerConnectionState.Connecting,
            "● 已就绪：这只是显示文字");
        CheckTrue("readiness is driven by state instead of localized text", readyWithArbitraryText.IsReady);
        CheckFalse("ready-looking text cannot forge readiness", connectingWithReadyWords.IsReady);

        var readyEventName = @"Local\QingTab.Tests.Ready." + Guid.NewGuid().ToString("N");
        var missingReadyEventName = @"Local\QingTab.Tests.Missing." + Guid.NewGuid().ToString("N");
        CheckTrue("cold-start probe reports a missing resident without waiting",
            InstanceReadiness.Probe(missingReadyEventName) == InstanceReadinessState.Missing);
        using (var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyEventName))
        {
            CheckTrue("cold-start probe distinguishes a resident that is still starting",
                InstanceReadiness.Probe(readyEventName) == InstanceReadinessState.Starting);
            readyEvent.Set();
            CheckTrue("cold-start probe reports a ready resident",
                InstanceReadiness.Probe(readyEventName) == InstanceReadinessState.Ready);
            readyEvent.Reset();

            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(30);
                readyEvent.Set();
            });
            CheckTrue("cold-start client waits for resident readiness",
                InstanceReadiness.WaitUntilReady(readyEventName, timeoutMilliseconds: 1_000));

            readyEvent.Reset();
            CheckFalse("readiness wait has a bounded timeout",
                InstanceReadiness.WaitUntilReady(readyEventName, timeoutMilliseconds: 20));
        }

        var testPipeName = "QingTab.Tests.OpenTab." + Guid.NewGuid().ToString("N");
        var ipcCalls = 0;
        using (var ipcServer = new OpenTabIpc.Server(
                   testPipeName,
                   path =>
                   {
                       ipcCalls++;
                       return path.EndsWith("Duplicate", StringComparison.Ordinal)
                           ? OpenTabIpcResponse.Duplicate
                           : path.EndsWith("Rejected", StringComparison.Ordinal)
                               ? OpenTabIpcResponse.Rejected
                               : OpenTabIpcResponse.Accepted;
                   }))
        {
            ipcServer.Start();
            CheckTrue("IPC acknowledges an accepted request",
                OpenTabIpc.TrySend(
                    testPipeName,
                    @"C:\Accepted",
                    timeoutMilliseconds: 1_000,
                    response: out var acceptedResponse,
                    error: out _)
                && acceptedResponse == OpenTabIpcResponse.Accepted);
            CheckTrue("IPC acknowledges a duplicate request without opening a fallback",
                OpenTabIpc.TrySend(
                    testPipeName,
                    @"C:\Duplicate",
                    timeoutMilliseconds: 1_000,
                    response: out var duplicateResponse,
                    error: out _)
                && duplicateResponse == OpenTabIpcResponse.Duplicate);
            CheckTrue("IPC exposes a rejected request so the client can use the native fallback",
                OpenTabIpc.TrySend(
                    testPipeName,
                    @"C:\Rejected",
                    timeoutMilliseconds: 1_000,
                    response: out var rejectedResponse,
                    error: out _)
                && rejectedResponse == OpenTabIpcResponse.Rejected);
            CheckTrue("IPC invokes the public request seam once per acknowledged request", ipcCalls == 3);
        }

        var stalledPipeName = "QingTab.Tests.Stalled." + Guid.NewGuid().ToString("N");
        using (var stalledServer = new NamedPipeServerStream(
                   stalledPipeName,
                   PipeDirection.InOut,
                   1,
                   PipeTransmissionMode.Byte,
                   PipeOptions.Asynchronous))
        {
            var stalledServerTask = Task.Run(() =>
            {
                stalledServer.WaitForConnection();
                using var reader = new BinaryReader(stalledServer, System.Text.Encoding.UTF8, leaveOpen: true);
                _ = reader.ReadString();
                Thread.Sleep(400);
                try
                {
                    stalledServer.Disconnect();
                }
                catch (IOException)
                {
                    // The timed-out client may already have closed its end.
                }
            });
            var stalledStopwatch = Stopwatch.StartNew();
            var stalledSendSucceeded = OpenTabIpc.TrySend(
                stalledPipeName,
                @"C:\NoAcknowledgement",
                timeoutMilliseconds: 75,
                response: out _,
                error: out _);
            stalledStopwatch.Stop();
            CheckFalse("IPC fails when an accepted connection never acknowledges", stalledSendSucceeded);
            CheckTrue("IPC acknowledgement wait obeys the caller's bounded timeout",
                stalledStopwatch.ElapsedMilliseconds < 300);
            stalledServerTask.Wait(1_000);
        }

        var backgroundStartInfo = BackgroundInstanceLauncher.CreateStartInfo(
            @"C:\Apps\QingTab.exe",
            skipRegistrationRepair: true);
        Check("background instance receives isolated-test argument",
            "--startup --no-registration-repair",
            backgroundStartInfo.Arguments);
        CheckTrue("background instance uses a hidden Shell launch",
            backgroundStartInfo.UseShellExecute
            && backgroundStartInfo.WindowStyle == System.Diagnostics.ProcessWindowStyle.Hidden);
        CheckTrue("background instance does not request inheritable redirected pipes",
            !backgroundStartInfo.RedirectStandardInput
            && !backgroundStartInfo.RedirectStandardOutput
            && !backgroundStartInfo.RedirectStandardError);

        if (Failures.Count == 0)
        {
            Console.WriteLine($"PASS: {Checks} QingTab behavior checks");
            return 0;
        }

        foreach (var failure in Failures)
            Console.Error.WriteLine(failure);
        return 1;
    }

    private static void Check(string name, string expected, string actual)
    {
        Checks++;
        if (string.Equals(expected, actual, StringComparison.Ordinal)) return;
        Failures.Add($"FAIL: {name}; expected <{expected}> but got <{actual}>");
    }

    private static void CheckTrue(string name, bool condition)
    {
        Checks++;
        if (condition) return;
        Failures.Add($"FAIL: {name}; expected true but got false");
    }

    private static void CheckFalse(string name, bool condition)
    {
        Checks++;
        if (!condition) return;
        Failures.Add($"FAIL: {name}; expected false but got true");
    }

    private static ExplorerTabActivationLease CreateRestoredActivationLease(int windowHandle)
    {
        var lease = new ExplorerTabActivationLease(
            new IntPtr(windowHandle),
            new IntPtr(windowHandle + 1),
            new[] { "tab-a", "tab-b" },
            "tab-a");
        if (!lease.TryBindCreatedTab(
                new IntPtr(windowHandle + 3),
                new TabStripObservation(
                    new IntPtr(windowHandle + 3),
                    new[] { "tab-a", "tab-b", "tab-new" },
                    "tab-new",
                    targetWindowIsForeground: true))
            || !lease.TryAuthorizeOriginalRestore(
                new TabStripObservation(
                    new IntPtr(windowHandle + 3),
                    new[] { "tab-a", "tab-b", "tab-new" },
                    "tab-new",
                    targetWindowIsForeground: true))
            || !lease.ObserveOriginalRestore(
                new TabStripObservation(
                    new IntPtr(windowHandle + 1),
                    new[] { "tab-a", "tab-b", "tab-new" },
                    "tab-a",
                    targetWindowIsForeground: true)))
            throw new InvalidOperationException("The activation-lease fixture could not be created.");
        return lease;
    }
}
