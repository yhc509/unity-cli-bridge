#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using UnityCli.Protocol;
using UnityCliBridge.Bridge;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed partial class QaCommandHandler
    {
        private enum SequencePhase
        {
            WaitCondition,
            ExecuteActions,
        }

        private static void StartRunSequenceDeferred(
            string argumentsJson,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId)
        {
            QaRunSequenceArgs args = ProtocolJson.Deserialize<QaRunSequenceArgs>(argumentsJson) ?? new QaRunSequenceArgs();
            QaSequenceStep[] steps = args.steps ?? Array.Empty<QaSequenceStep>();
            if (steps.Length == 0)
            {
                throw new CommandFailureException("QA_SEQUENCE_EMPTY", "qa run-sequence requires at least one step.", false, null);
            }

            int overallTimeoutMs = args.timeoutMs > 0 ? args.timeoutMs : ProtocolConstants.DefaultQaRunSequenceTimeoutMs;
            if (overallTimeoutMs > ProtocolConstants.MaxQaRunSequenceTimeoutMs)
            {
                throw new CommandFailureException(
                    "QA_SEQUENCE_TIMEOUT_RANGE",
                    $"qa run-sequence timeout must be <= {ProtocolConstants.MaxQaRunSequenceTimeoutMs}ms.",
                    false,
                    null);
            }

            var stopwatch = Stopwatch.StartNew();
            int stepIndex = 0;
            SequencePhase phase = SequencePhase.WaitCondition;
            int actionIndex = 0;
            IQaSwipeOperation? activeSwipe = null;
            long actionDelayUntilMs = 0;
            long stepStartMs = 0;
            bool baselineCaptured = false;
            var changedBaselines = new Dictionary<int, string>();
            string recordingPath = string.Empty;
            bool recordingStarted = false;
            string profileCaptureId = string.Empty;
            bool profileStarted = false;

            void Poll()
            {
                if (completion.Task.IsCompleted)
                {
                    EditorTickPump.Remove(Poll);
                    AbortActiveSwipe();
                    StopSequenceRecordingIfNeeded();
                    StopSequenceProfileIfNeeded();
                    return;
                }

                try
                {
                    EnsureDeferredPlayMode();
                    long nowMs = stopwatch.ElapsedMilliseconds;

                    if (nowMs >= overallTimeoutMs)
                    {
                        QaSequenceStep current = steps[Math.Min(stepIndex, steps.Length - 1)];
                        EvaluateConditions(GetConditions(current), changedBaselines, out var unmet, out var snapshot);
                        FailTimeout(stepIndex, current, unmet, snapshot);
                        return;
                    }

                    QaSequenceStep step = steps[stepIndex];
                    if (!baselineCaptured)
                    {
                        CaptureChangedBaselines(GetConditions(step), changedBaselines);
                        baselineCaptured = true;
                        stepStartMs = nowMs;
                    }

                    int stepTimeoutMs = step.timeoutMs > 0
                        ? step.timeoutMs
                        : ProtocolConstants.DefaultQaRunSequenceStepTimeoutMs;

                    if (phase == SequencePhase.WaitCondition)
                    {
                        if (EvaluateConditions(GetConditions(step), changedBaselines, out var unmet, out var snapshot))
                        {
                            phase = SequencePhase.ExecuteActions;
                            actionIndex = 0;
                            actionDelayUntilMs = 0;
                            return;
                        }

                        if (nowMs - stepStartMs >= stepTimeoutMs)
                        {
                            FailTimeout(stepIndex, step, unmet, snapshot);
                        }

                        return;
                    }

                    QaSequenceAction[] actions = GetActions(step);
                    if (actionIndex >= actions.Length)
                    {
                        stepIndex++;
                        phase = SequencePhase.WaitCondition;
                        baselineCaptured = false;
                        changedBaselines.Clear();
                        if (stepIndex >= steps.Length)
                        {
                            CompleteSuccess();
                        }

                        return;
                    }

                    if (nowMs - stepStartMs >= stepTimeoutMs)
                    {
                        EvaluateConditions(GetConditions(step), changedBaselines, out var unmet, out var snapshot);
                        unmet.Add(CreatePendingActionUnmet(actions[actionIndex], actionIndex));
                        FailTimeout(stepIndex, step, unmet, snapshot);
                        return;
                    }

                    bool actionDone = AdvanceAction(actions[actionIndex], ref activeSwipe, ref actionDelayUntilMs, nowMs);
                    if (actionDone)
                    {
                        actionIndex++;
                    }
                }
                catch (Exception exception)
                {
                    CompleteFailure(exception);
                }
            }

            void CompleteSuccess()
            {
                EditorTickPump.Remove(Poll);
                AbortActiveSwipe();
                stopwatch.Stop();
                string finalRecordingPath = StopSequenceRecordingIfNeeded();
                StopSequenceProfileIfNeeded();
                completion.TrySetResult(CreateSuccessResponse(requestId, projectHash, new QaRunSequencePayload
                {
                    status = "Completed",
                    completedSteps = steps.Length,
                    totalSteps = steps.Length,
                    hasFailure = false,
                    elapsedMs = stopwatch.ElapsedMilliseconds,
                    recordingPath = finalRecordingPath,
                    profileCaptureId = profileCaptureId,
                }, stopwatch.ElapsedMilliseconds));
            }

            void FailTimeout(
                int index,
                QaSequenceStep step,
                List<QaSequenceUnmet> unmet,
                List<QaSequenceSnapshotEntry> snapshot)
            {
                EditorTickPump.Remove(Poll);
                AbortActiveSwipe();
                stopwatch.Stop();
                string finalRecordingPath = StopSequenceRecordingIfNeeded();
                StopSequenceProfileIfNeeded();
                completion.TrySetResult(CreateSuccessResponse(requestId, projectHash, new QaRunSequencePayload
                {
                    status = "TimedOut",
                    completedSteps = Math.Max(0, Math.Min(index, steps.Length)),
                    totalSteps = steps.Length,
                    hasFailure = true,
                    failedStep = new QaSequenceFailure
                    {
                        index = index,
                        name = step.name,
                        unmet = unmet.ToArray(),
                        stateSnapshot = snapshot.ToArray(),
                    },
                    elapsedMs = stopwatch.ElapsedMilliseconds,
                    recordingPath = finalRecordingPath,
                    profileCaptureId = profileCaptureId,
                }, stopwatch.ElapsedMilliseconds));
            }

            void CompleteFailure(Exception exception)
            {
                EditorTickPump.Remove(Poll);
                AbortActiveSwipe();
                stopwatch.Stop();
                StopSequenceRecordingIfNeeded();
                completion.TrySetResult(CreateFailureResponse(requestId, projectHash, exception, stopwatch.ElapsedMilliseconds));
            }

            string StopSequenceRecordingIfNeeded()
            {
                if (!args.record || !recordingStarted)
                {
                    return recordingPath;
                }

                try
                {
                    recordingPath = RecordCommandHandler.StopForSequenceIfActive() ?? recordingPath;
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError("[UCB] qa run-sequence recording stop failed: " + ex);
                }

                recordingStarted = false;
                return recordingPath;
            }

            void StopSequenceProfileIfNeeded()
            {
                if (!args.profile || !profileStarted)
                {
                    return;
                }

                try
                {
                    ProfileCommandHandler.StopForSequenceIfActive();
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError("[UCB] qa run-sequence profile stop failed: " + ex);
                }

                profileStarted = false;
            }

            void AbortActiveSwipe()
            {
                activeSwipe?.Abort();
                activeSwipe = null;
            }

            if (args.record && EditorApplication.isPlaying)
            {
                RecordCommandHandler.StartForSequence(args.recordPath);
                recordingStarted = true;
            }

            if (args.profile && EditorApplication.isPlaying)
            {
                try
                {
                    profileCaptureId = ProfileCommandHandler.StartForSequence();
                    profileStarted = !string.IsNullOrEmpty(profileCaptureId);
                }
                catch (Exception ex)
                {
                    UnityEngine.Debug.LogError("[UCB] qa run-sequence profile start failed: " + ex);
                }
            }

            EditorTickPump.Add(Poll);
            Poll();
        }

        private static bool EvaluateConditions(
            QaSequenceCondition[] conditions,
            Dictionary<int, string> baselines,
            out List<QaSequenceUnmet> unmet,
            out List<QaSequenceSnapshotEntry> snapshot)
        {
            unmet = new List<QaSequenceUnmet>();
            snapshot = new List<QaSequenceSnapshotEntry>();

            for (int i = 0; i < conditions.Length; i++)
            {
                QaSequenceCondition condition = conditions[i];
                string actual = ReadActual(condition);
                snapshot.Add(new QaSequenceSnapshotEntry
                {
                    target = condition.target,
                    key = condition.key,
                    value = actual,
                });

                if (!EvaluateOne(condition, actual, baselines, i))
                {
                    unmet.Add(new QaSequenceUnmet
                    {
                        target = condition.target,
                        kind = condition.kind,
                        key = condition.key,
                        op = condition.op,
                        expected = GetExpectedValue(condition),
                        actual = actual,
                    });
                }
            }

            return unmet.Count == 0;
        }

        private static bool EvaluateOne(
            QaSequenceCondition condition,
            string actual,
            Dictionary<int, string> baselines,
            int index)
        {
            switch (condition.kind)
            {
                case "active":
                case "gone":
                case "interactable":
                    return string.Equals(actual, GetExpectedBoolValue(condition), StringComparison.OrdinalIgnoreCase);
                case "scene":
                    return string.Equals(actual, condition.value, StringComparison.OrdinalIgnoreCase);
                case "log":
                    return !string.Equals(actual, "<not found>", StringComparison.Ordinal);
                case "transform":
                case "query":
                    if (string.Equals(condition.op, "changed", StringComparison.Ordinal))
                    {
                        return baselines.TryGetValue(index, out string baseline)
                            && !string.Equals(actual, baseline, StringComparison.Ordinal);
                    }

                    return QaConditionOps.Evaluate(actual, condition.op, condition.value, condition.epsilon);
                default:
                    return false;
            }
        }

        private static string ReadActual(QaSequenceCondition condition)
        {
            switch (condition.kind)
            {
                case "active":
                    return ResolveSequenceTarget(condition.target, out GameObject? activeTarget) && activeTarget != null && activeTarget.activeInHierarchy
                        ? "true"
                        : "false";
                case "gone":
                    return ResolveSequenceTarget(condition.target, out GameObject? goneTarget) && goneTarget != null
                        ? "false"
                        : "true";
                case "transform":
                    return ReadTransformActual(condition);
                case "scene":
                    return SceneManager.GetActiveScene().name;
                case "log":
                    return ReadLogActual(condition.value);
                case "interactable":
                    return ResolveSequenceTarget(condition.target, out GameObject? interactableTarget)
                        && interactableTarget != null
                        && GetInteractableValue(interactableTarget)
                        ? "true"
                        : "false";
                case "query":
                    return ReadQueryActual(condition);
                default:
                    return string.Empty;
            }
        }

        private static string ReadTransformActual(QaSequenceCondition condition)
        {
            if (!ResolveSequenceTarget(condition.target, out GameObject? target) || target == null)
            {
                return "<target not found>";
            }

            return condition.key switch
            {
                "position" => FormatVector3(target.transform.position),
                "rotation" => FormatVector3(target.transform.eulerAngles),
                "scale" => FormatVector3(target.transform.localScale),
                _ => "<unknown transform key>",
            };
        }

        private static string ReadLogActual(string expected)
        {
            ConsoleLogEntry[] entries = ConsoleLogBuffer.Read(100, string.Empty);
            foreach (ConsoleLogEntry entry in entries)
            {
                if (entry.message.IndexOf(expected, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return entry.message;
                }
            }

            return "<not found>";
        }

        private static string ReadQueryActual(QaSequenceCondition condition)
        {
            if (!ResolveSequenceTarget(condition.target, out GameObject? target) || target == null)
            {
                return "<target not found>";
            }

            foreach (IQaQueryable queryable in target.GetComponents<IQaQueryable>())
            {
                if (queryable is Behaviour behaviour && !behaviour.isActiveAndEnabled)
                {
                    continue;
                }

                if (queryable.TryQaQuery(condition.key, out object? value))
                {
                    return NormalizeQueryValue(value);
                }
            }

            return $"<no IQaQueryable: {condition.key}>";
        }

        private static void CaptureChangedBaselines(
            QaSequenceCondition[] conditions,
            Dictionary<int, string> baselines)
        {
            baselines.Clear();
            for (int i = 0; i < conditions.Length; i++)
            {
                QaSequenceCondition condition = conditions[i];
                if (string.Equals(condition.op, "changed", StringComparison.Ordinal))
                {
                    baselines[i] = ReadActual(condition);
                }
            }
        }

        private static bool ResolveSequenceTarget(string target, out GameObject? gameObject)
        {
            return (QaTargetRegistry.TryResolve(target, out gameObject) && gameObject != null)
                || (QaTargetRegistry.TryResolvePath(target, out gameObject) && gameObject != null);
        }

        private static string GetExpectedValue(QaSequenceCondition condition)
        {
            if (string.Equals(condition.op, "changed", StringComparison.Ordinal))
            {
                return "<changed>";
            }

            return condition.kind switch
            {
                "active" or "gone" or "interactable" => GetExpectedBoolValue(condition),
                _ => condition.value,
            };
        }

        private static QaSequenceUnmet CreatePendingActionUnmet(QaSequenceAction action, int actionIndex)
        {
            return new QaSequenceUnmet
            {
                target = action.target,
                kind = "action",
                key = $"{actionIndex}:{action.kind}",
                op = "complete",
                expected = "done",
                actual = "pending",
            };
        }

        private static string GetExpectedBoolValue(QaSequenceCondition condition)
        {
            return string.IsNullOrWhiteSpace(condition.value) ? "true" : condition.value;
        }

        private static string NormalizeQueryValue(object? value)
        {
            return value switch
            {
                null => string.Empty,
                bool boolValue => boolValue ? "true" : "false",
                string stringValue => stringValue,
                float floatValue => floatValue.ToString("G9", CultureInfo.InvariantCulture),
                double doubleValue => doubleValue.ToString("G17", CultureInfo.InvariantCulture),
                decimal decimalValue => decimalValue.ToString("G29", CultureInfo.InvariantCulture),
                Vector2 vector2 => FormatVector2(vector2),
                Vector3 vector3 => FormatVector3(vector3),
                IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? string.Empty,
            };
        }

        private static string FormatVector2(Vector2 value)
        {
            return string.Join(
                ",",
                value.x.ToString("G9", CultureInfo.InvariantCulture),
                value.y.ToString("G9", CultureInfo.InvariantCulture));
        }

        private static string FormatVector3(Vector3 value)
        {
            return string.Join(
                ",",
                value.x.ToString("G9", CultureInfo.InvariantCulture),
                value.y.ToString("G9", CultureInfo.InvariantCulture),
                value.z.ToString("G9", CultureInfo.InvariantCulture));
        }

        private static QaSequenceCondition[] GetConditions(QaSequenceStep step)
        {
            return step.wait ?? Array.Empty<QaSequenceCondition>();
        }

        private static QaSequenceAction[] GetActions(QaSequenceStep step)
        {
            return step.actions ?? Array.Empty<QaSequenceAction>();
        }

        private static bool AdvanceAction(
            QaSequenceAction action,
            ref IQaSwipeOperation? activeSwipe,
            ref long actionDelayUntilMs,
            long nowMs)
        {
            switch (action.kind)
            {
                case "key":
                    QaInputSimulator.SimulateKey(action.key);
                    return true;
                case "tap":
                    if (action.hasTapCoords)
                    {
                        QaInputSimulator.SimulateTap(new Vector2(action.x, action.y));
                        return true;
                    }

                    HandleTapTarget(new QaTapArgs { target = action.target });
                    return true;
                case "wait":
                    if (actionDelayUntilMs == 0)
                    {
                        actionDelayUntilMs = nowMs + action.waitMs;
                    }

                    if (nowMs >= actionDelayUntilMs)
                    {
                        actionDelayUntilMs = 0;
                        return true;
                    }

                    return false;
                case "swipe":
                    activeSwipe ??= QaInputSimulator.BeginSwipe(
                        new Vector2(action.fromX, action.fromY),
                        new Vector2(action.toX, action.toY),
                        action.durationMs);
                    if (activeSwipe.Advance())
                    {
                        activeSwipe = null;
                        return true;
                    }

                    return false;
                default:
                    throw new CommandFailureException("QA_SEQUENCE_BAD_ACTION", $"Unknown action kind '{action.kind}'.", false, null);
            }
        }
    }
}
