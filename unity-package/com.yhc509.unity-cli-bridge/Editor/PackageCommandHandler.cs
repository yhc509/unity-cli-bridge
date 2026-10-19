#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using UnityCli.Protocol;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed class PackageCommandHandler
    {
        internal const int PackageRequestTimeoutSeconds = ProtocolConstants.DefaultPackageRequestTimeoutSeconds;
        private static readonly object _activeLock = new object();
        private static bool _hasActiveRequest;

        public bool CanHandle(string command)
        {
            return ProtocolHelpers.IsPackageCommand(command);
        }

        public string Handle(string command, string argumentsJson)
        {
            if (IsDeferred(command, argumentsJson))
            {
                throw new InvalidOperationException("Deferred package command must be started through StartDeferred: " + command);
            }

            throw new InvalidOperationException("Unsupported package command: " + command);
        }

        // argumentsJson is retained for interface compatibility with BridgeHost command dispatch.
        public bool IsDeferred(string command, string? argumentsJson = null)
        {
            return ProtocolHelpers.IsDeferredPackageCommand(command);
        }

        public void StartDeferred(
            string command,
            string argumentsJson,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash)
        {
            if (completion.Task.IsCompleted)
            {
                return;
            }

            string requestId = GetRequestId(completion);
            var stopwatch = Stopwatch.StartNew();
            if (!TryBeginActiveRequest())
            {
                stopwatch.Stop();
                completion.TrySetResult(ResponseEnvelope.Failure(
                    requestId,
                    projectHash,
                    ProtocolConstants.ErrorPackageBusy,
                    ProtocolConstants.PackageBusyMessage,
                    true,
                    stopwatch.ElapsedMilliseconds,
                    ProtocolConstants.TransportLive,
                    null));
                return;
            }

            try
            {
                if (string.Equals(command, ProtocolConstants.CommandPackageList, StringComparison.Ordinal))
                {
                    PackageListArgs args = ProtocolJson.Deserialize<PackageListArgs>(argumentsJson) ?? new PackageListArgs();
                    ListRequest request = Client.List(true);
                    StartPollingRequest(
                        request,
                        completedRequest => CreateListPayloadJson(completedRequest, args),
                        "PACKAGE_LIST_FAILED",
                        "Failed to list packages.",
                        completion,
                        projectHash,
                        requestId,
                        stopwatch);
                    return;
                }

                if (string.Equals(command, ProtocolConstants.CommandPackageAdd, StringComparison.Ordinal))
                {
                    PackageAddArgs args = ProtocolJson.Deserialize<PackageAddArgs>(argumentsJson) ?? new PackageAddArgs();
                    if (string.IsNullOrWhiteSpace(args.name))
                    {
                        throw new CommandFailureException("INVALID_ARGS", "A package name is required.", false, null);
                    }

                    string identifier = !string.IsNullOrWhiteSpace(args.version)
                        ? $"{args.name}@{args.version}"
                        : args.name;

                    AddRequest request = Client.Add(identifier);
                    StartPollingRequest(
                        request,
                        CreateAddPayloadJson,
                        "PACKAGE_ADD_FAILED",
                        $"Failed to add package: {identifier}",
                        completion,
                        projectHash,
                        requestId,
                        stopwatch);
                    return;
                }

                if (string.Equals(command, ProtocolConstants.CommandPackageRemove, StringComparison.Ordinal))
                {
                    PackageRemoveArgs args = ProtocolJson.Deserialize<PackageRemoveArgs>(argumentsJson) ?? new PackageRemoveArgs();
                    if (!args.force)
                    {
                        throw new CommandFailureException(ProtocolConstants.ErrorPackageForceRequired, "Removing a package requires --force.");
                    }

                    if (string.IsNullOrWhiteSpace(args.name))
                    {
                        throw new CommandFailureException("INVALID_ARGS", "A package name is required.", false, null);
                    }

                    RemoveRequest request = Client.Remove(args.name);
                    StartPollingRequest(
                        request,
                        _ => CreateRemovePayloadJson(args.name),
                        "PACKAGE_REMOVE_FAILED",
                        $"Failed to remove package: {args.name}",
                        completion,
                        projectHash,
                        requestId,
                        stopwatch);
                    return;
                }

                if (string.Equals(command, ProtocolConstants.CommandPackageSearch, StringComparison.Ordinal))
                {
                    PackageSearchArgs args = ProtocolJson.Deserialize<PackageSearchArgs>(argumentsJson) ?? new PackageSearchArgs();
                    if (string.IsNullOrWhiteSpace(args.query))
                    {
                        throw new CommandFailureException("INVALID_ARGS", "A search query is required.", false, null);
                    }

                    string query = args.query.Trim();
                    SearchRequest request = Client.SearchAll();
                    StartPollingRequest(
                        request,
                        completedRequest => CreateSearchPayloadJson(completedRequest, query),
                        "PACKAGE_SEARCH_FAILED",
                        $"Package search failed: {query}",
                        completion,
                        projectHash,
                        requestId,
                        stopwatch);
                    return;
                }

                throw new InvalidOperationException("Unsupported package command: " + command);
            }
            catch
            {
                EndActiveRequest();
                throw;
            }
        }

        private static string CreateListPayloadJson(ListRequest request, PackageListArgs args)
        {
            var records = new List<PackageRecord>();
            foreach (var package in request.Result)
            {
                records.Add(new PackageRecord
                {
                    name = package.name,
                    version = package.version,
                    displayName = package.displayName ?? package.name,
                    source = package.source.ToString(),
                });
            }

            return ProtocolJson.Serialize(new PackageListPayload
            {
                packages = PackageListFilterUtility.ApplyPackageListFilter(records, args),
            });
        }

        private static string CreateAddPayloadJson(AddRequest request)
        {
            return ProtocolJson.Serialize(new PackageMutationPayload
            {
                name = request.Result.name,
                version = request.Result.version,
                added = true,
            });
        }

        private static string CreateRemovePayloadJson(string packageName)
        {
            return ProtocolJson.Serialize(new PackageMutationPayload
            {
                name = packageName,
                removed = true,
            });
        }

        private static string CreateSearchPayloadJson(SearchRequest request, string query)
        {
            var records = new List<PackageRecord>();
            foreach (var package in request.Result)
            {
                string displayName = package.displayName ?? package.name;
                if (package.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0
                    && displayName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                records.Add(new PackageRecord
                {
                    name = package.name,
                    version = package.versions.latest ?? string.Empty,
                    displayName = displayName,
                    source = package.source.ToString(),
                });
            }

            return ProtocolJson.Serialize(new PackageSearchPayload
            {
                results = records.OrderBy(record => record.name, StringComparer.OrdinalIgnoreCase).ToArray(),
            });
        }

        private static void StartPollingRequest<TRequest>(
            TRequest request,
            Func<TRequest, string> createPayloadJson,
            string failureCode,
            string fallbackFailureMessage,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId,
            Stopwatch stopwatch)
            where TRequest : Request
        {
            bool isFinished = false;

            void StopPolling()
            {
                if (isFinished)
                {
                    return;
                }

                isFinished = true;
                EditorTickPump.Remove(Poll);
                stopwatch.Stop();
            }

            void FinishPolling()
            {
                StopPolling();
                EndActiveRequest();
            }

            void FinishPollingAfterTimeout()
            {
                StopPolling();
                StartBackgroundActiveRequestTracker(request);
            }

            void Poll()
            {
                if (completion.Task.IsCompleted)
                {
                    FinishPolling();
                    return;
                }

                try
                {
                    if (request.IsCompleted)
                    {
                        if (request.Status == StatusCode.Failure)
                        {
                            completion.TrySetResult(ResponseEnvelope.Failure(
                                requestId,
                                projectHash,
                                failureCode,
                                request.Error?.message ?? fallbackFailureMessage,
                                false,
                                stopwatch.ElapsedMilliseconds,
                                ProtocolConstants.TransportLive,
                                ProtocolErrorDetails.FromString(request.Error?.errorCode.ToString())));
                            FinishPolling();
                            return;
                        }

                        completion.TrySetResult(ResponseEnvelope.Success(
                            requestId,
                            projectHash,
                            createPayloadJson(request),
                            stopwatch.ElapsedMilliseconds,
                            ProtocolConstants.TransportLive));
                        FinishPolling();
                        return;
                    }

                    if (ProtocolConstants.IsPackageRequestTimedOut(stopwatch.Elapsed, PackageRequestTimeoutSeconds))
                    {
                        completion.TrySetResult(ResponseEnvelope.Failure(
                            requestId,
                            projectHash,
                            ProtocolConstants.ErrorPackageTimeout,
                            ProtocolConstants.BuildPackageRequestTimeoutMessage(PackageRequestTimeoutSeconds),
                            false,
                            stopwatch.ElapsedMilliseconds,
                            ProtocolConstants.TransportLive,
                            null));
                        FinishPollingAfterTimeout();
                    }
                }
                catch (Exception exception)
                {
                    completion.TrySetResult(CreateFailureResponse(requestId, projectHash, exception, stopwatch.ElapsedMilliseconds));
                    FinishPolling();
                }
            }

            EditorTickPump.Add(Poll);
            Poll();
        }

        private static void StartBackgroundActiveRequestTracker(Request request)
        {
            ActiveRequestCompletionTracker? tracker = null;

            void BackgroundPoll()
            {
                tracker!.Poll();
            }

            tracker = new ActiveRequestCompletionTracker(
                () => request.IsCompleted,
                () => EditorTickPump.Remove(BackgroundPoll),
                EndActiveRequest);

            EditorTickPump.Add(BackgroundPoll);
            BackgroundPoll();
        }

        internal sealed class ActiveRequestCompletionTracker
        {
            private readonly Func<bool> _isCompleted;
            private readonly Action _stopTracking;
            private readonly Action _endActiveRequest;
            private bool _isEnded;

            internal ActiveRequestCompletionTracker(
                Func<bool> isCompleted,
                Action stopTracking,
                Action endActiveRequest)
            {
                _isCompleted = isCompleted;
                _stopTracking = stopTracking;
                _endActiveRequest = endActiveRequest;
            }

            internal void Poll()
            {
                if (_isEnded || !_isCompleted())
                {
                    return;
                }

                _isEnded = true;
                _stopTracking();
                _endActiveRequest();
            }
        }

        internal static bool TryBeginActiveRequestForTesting()
        {
            return TryBeginActiveRequest();
        }

        internal static void EndActiveRequestForTesting()
        {
            EndActiveRequest();
        }

        internal static void ResetActiveRequestForTesting()
        {
            lock (_activeLock)
            {
                _hasActiveRequest = false;
            }
        }

        internal static bool HasActiveRequestForTesting()
        {
            lock (_activeLock)
            {
                return _hasActiveRequest;
            }
        }

        private static bool TryBeginActiveRequest()
        {
            lock (_activeLock)
            {
                if (_hasActiveRequest)
                {
                    return false;
                }

                _hasActiveRequest = true;
                return true;
            }
        }

        private static void EndActiveRequest()
        {
            lock (_activeLock)
            {
                _hasActiveRequest = false;
            }
        }

        private static string GetRequestId(TaskCompletionSource<ResponseEnvelope> completion)
        {
            string? requestId = completion.Task.AsyncState as string;
            if (string.IsNullOrWhiteSpace(requestId))
            {
                throw new InvalidOperationException("Deferred package request ID is missing.");
            }

            return requestId;
        }

        private static ResponseEnvelope CreateFailureResponse(
            string requestId,
            string projectHash,
            Exception exception,
            long durationMs)
        {
            if (exception is CommandFailureException failure)
            {
                return ResponseEnvelope.Failure(
                    requestId,
                    projectHash,
                    failure.ErrorCode,
                    failure.Message,
                    failure.IsRetryable,
                    durationMs,
                    ProtocolConstants.TransportLive,
                    failure.Details);
            }

            return ResponseEnvelope.Failure(
                requestId,
                projectHash,
                "COMMAND_FAILED",
                exception.Message,
                false,
                durationMs,
                ProtocolConstants.TransportLive,
                ProtocolErrorDetails.FromString(exception.ToString()));
        }
    }
}
