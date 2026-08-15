using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using QingTab.Helpers;

namespace QingTab.Hooks;

/// <summary>
/// The observable result of submitting one navigation to Explorer. A busy
/// rejection is known not to have run and may be retried; every other exception
/// raised after the cross-process call starts has an unknown outcome and must
/// never trigger a duplicate fallback window.
/// </summary>
public readonly struct ExplorerNavigationSubmissionResult
{
    public ExplorerNavigationSubmissionResult(
        ExplorerNavigationDisposition disposition,
        int? hresult,
        ExplorerComFailureKind? failureKind = null,
        bool exactIdentityPreserved = true)
    {
        Disposition = disposition;
        HResult = hresult;
        FailureKind = failureKind;
        ExactIdentityPreserved = exactIdentityPreserved;
    }

    public ExplorerNavigationDisposition Disposition { get; }
    public int? HResult { get; }
    public ExplorerComFailureKind? FailureKind { get; }
    public bool ExactIdentityPreserved { get; }
}

/// <summary>
/// Executes the at-most-once-sensitive part of Explorer navigation. The
/// caller supplies the exact COM call and a fresh identity guard used only
/// between known busy rejections.
/// </summary>
public static class ExplorerNavigationSubmission
{
    public static async Task<ExplorerNavigationSubmissionResult> SubmitAsync(
        Action submit,
        RequestTimeBudget budget,
        Func<bool> validateExactIdentityBeforeRetry)
    {
        if (submit == null) throw new ArgumentNullException(nameof(submit));
        if (budget == null) throw new ArgumentNullException(nameof(budget));
        if (validateExactIdentityBeforeRetry == null)
            throw new ArgumentNullException(nameof(validateExactIdentityBeforeRetry));

        for (var failedAttempt = 0; ; failedAttempt++)
        {
            try
            {
                // From this exact point forward an exception cannot prove
                // that Explorer rejected the cross-process call.
                submit();
                return new ExplorerNavigationSubmissionResult(
                    ExplorerNavigationDisposition.Accepted,
                    null);
            }
            catch (COMException ex) when (
                ExplorerComPolicy.Classify(ex.HResult) == ExplorerComFailureKind.Busy)
            {
                var retryDelay = ExplorerComPolicy.GetRetryDelayMilliseconds(failedAttempt);
                var boundedDelay = budget.LimitMilliseconds(retryDelay);
                if (retryDelay == 0 || boundedDelay == 0)
                    return new ExplorerNavigationSubmissionResult(
                        ExplorerNavigationDisposition.KnownRejected,
                        ex.HResult,
                        ExplorerComFailureKind.Busy);

                await Task.Delay(boundedDelay);
                if (budget.IsExpired)
                    return new ExplorerNavigationSubmissionResult(
                        ExplorerNavigationDisposition.KnownRejected,
                        ex.HResult,
                        ExplorerComFailureKind.Busy);

                try
                {
                    if (!validateExactIdentityBeforeRetry())
                        return new ExplorerNavigationSubmissionResult(
                            ExplorerNavigationDisposition.KnownRejected,
                            ex.HResult,
                            ExplorerComFailureKind.Busy,
                            exactIdentityPreserved: false);
                }
                catch (COMException validationException)
                {
                    return new ExplorerNavigationSubmissionResult(
                        ExplorerNavigationDisposition.KnownRejected,
                        validationException.HResult,
                        ExplorerComPolicy.Classify(validationException.HResult),
                        exactIdentityPreserved: false);
                }
                catch
                {
                    return new ExplorerNavigationSubmissionResult(
                        ExplorerNavigationDisposition.KnownRejected,
                        ex.HResult,
                        ExplorerComFailureKind.Other,
                        exactIdentityPreserved: false);
                }
            }
            catch (COMException ex)
            {
                return new ExplorerNavigationSubmissionResult(
                    ExplorerNavigationDisposition.Unknown,
                    ex.HResult,
                    ExplorerComPolicy.Classify(ex.HResult));
            }
            catch
            {
                return new ExplorerNavigationSubmissionResult(
                    ExplorerNavigationDisposition.Unknown,
                    null);
            }
        }
    }
}
