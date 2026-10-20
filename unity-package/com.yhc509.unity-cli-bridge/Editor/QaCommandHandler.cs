#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityCli.Protocol;
using UnityCliBridge.Bridge;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed partial class QaCommandHandler
    {
        private static readonly Dictionary<ulong, ScreenPositionContext> _screenPositionContextCache = new();
        private static bool _isScreenPositionCacheSubscribed;

        public QaCommandHandler()
        {
            QaTargetRegistry.EnsureSubscribed();
            EnsureScreenPositionCacheSubscribed();
        }

        public bool CanHandle(string command)
        {
            return string.Equals(command, ProtocolConstants.CommandQaClick, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandQaTap, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandQaSwipe, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandQaKey, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandQaUiDump, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandQaWorldDump, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandQaWaitUntil, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandQaRunSequence, StringComparison.Ordinal);
        }

        public string Handle(string command, string argumentsJson)
        {
            if (IsDeferred(command, argumentsJson))
            {
                throw new InvalidOperationException("Deferred QA command must be started through StartDeferred: " + command);
            }

            if (string.Equals(command, ProtocolConstants.CommandQaWorldDump, StringComparison.Ordinal)
                && !EditorApplication.isPlaying)
            {
                return HandleWorldDumpEditMode(argumentsJson);
            }

            RequirePlayMode();

            if (string.Equals(command, ProtocolConstants.CommandQaClick, StringComparison.Ordinal))
            {
                return HandleClick(argumentsJson);
            }

            if (string.Equals(command, ProtocolConstants.CommandQaTap, StringComparison.Ordinal))
            {
                return HandleTap(argumentsJson);
            }

            if (string.Equals(command, ProtocolConstants.CommandQaKey, StringComparison.Ordinal))
            {
                return HandleKey(argumentsJson);
            }

            if (string.Equals(command, ProtocolConstants.CommandQaUiDump, StringComparison.Ordinal))
            {
                return HandleUiDump(argumentsJson);
            }

            if (string.Equals(command, ProtocolConstants.CommandQaWorldDump, StringComparison.Ordinal))
            {
                return HandleWorldDump(argumentsJson);
            }

            if (string.Equals(command, ProtocolConstants.CommandQaSwipe, StringComparison.Ordinal))
            {
                return HandleSwipeOnTarget(argumentsJson);
            }

            throw new InvalidOperationException("Unhandled QA command: " + command);
        }

        // argumentsJson is retained for interface compatibility with BridgeHost command dispatch.
        public bool IsDeferred(string command, string? argumentsJson = null)
        {
            if (string.Equals(command, ProtocolConstants.CommandQaWaitUntil, StringComparison.Ordinal)
                || string.Equals(command, ProtocolConstants.CommandQaRunSequence, StringComparison.Ordinal))
            {
                return true;
            }

            if (!string.Equals(command, ProtocolConstants.CommandQaSwipe, StringComparison.Ordinal))
            {
                return false;
            }

            QaSwipeArgs args = string.IsNullOrWhiteSpace(argumentsJson)
                ? new QaSwipeArgs()
                : ProtocolJson.Deserialize<QaSwipeArgs>(argumentsJson) ?? new QaSwipeArgs();

            if (string.IsNullOrWhiteSpace(args.target))
            {
                return true;
            }

#if ENABLE_INPUT_SYSTEM
            // A legacy-only project drags UGUI targets synchronously, as it would without the package.
            return !QaLegacyInputSimulator.IsLegacyOnly;
#else
            return false;
#endif
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

            RequirePlayMode();
            string requestId = GetRequestId(completion);

            if (string.Equals(command, ProtocolConstants.CommandQaWaitUntil, StringComparison.Ordinal))
            {
                StartWaitUntilDeferred(argumentsJson, completion, projectHash, requestId);
                return;
            }

            if (string.Equals(command, ProtocolConstants.CommandQaRunSequence, StringComparison.Ordinal))
            {
                StartRunSequenceDeferred(argumentsJson, completion, projectHash, requestId);
                return;
            }

            if (string.Equals(command, ProtocolConstants.CommandQaSwipe, StringComparison.Ordinal))
            {
                StartSwipeDeferred(argumentsJson, completion, projectHash, requestId);
                return;
            }

            throw new InvalidOperationException("Unhandled deferred QA command: " + command);
        }

        private static void RequirePlayMode()
        {
            if (!EditorApplication.isPlaying)
            {
                throw new CommandFailureException("QA_NOT_PLAYING", "QA commands require Play Mode.", false, null);
            }
        }

        private static Vector2Int GetGameViewRenderSize()
        {
            Vector2 size = Handles.GetMainGameViewSize();
            return new Vector2Int((int)size.x, (int)size.y);
        }

        private static void WarnIfAspectMismatch(int screenshotWidth, int screenshotHeight, int screenWidth, int screenHeight)
        {
            if (QaCoordinateConverter.IsAspectMismatch(screenshotWidth, screenshotHeight, screenWidth, screenHeight))
            {
                UnityEngine.Debug.LogWarning(
                    $"[UnityCliBridge] qa coordinate aspect mismatch: screenshot {screenshotWidth}x{screenshotHeight} vs game view {screenWidth}x{screenHeight}. Coordinates may miss. Capture a fresh screenshot and try again.");
            }
        }

        private static string HandleClick(string argumentsJson)
        {
            QaClickArgs args = ProtocolJson.Deserialize<QaClickArgs>(argumentsJson) ?? new QaClickArgs();

            GameObject? target = null;
            string? resolvedQaId = null;

            if (!string.IsNullOrWhiteSpace(args.qaId))
            {
                resolvedQaId = args.qaId;
                if (!QaTargetRegistry.TryResolve(args.qaId!, out target) || target == null)
                {
                    throw new CommandFailureException("QA_TARGET_NOT_FOUND", $"No active GameObject found for QA ID '{args.qaId}'.", false, null);
                }
            }
            else if (!string.IsNullOrWhiteSpace(args.target))
            {
                if (!QaTargetRegistry.TryResolvePath(args.target!, out target) || target == null)
                {
                    throw new CommandFailureException("QA_TARGET_NOT_FOUND", $"No active GameObject found at path '{args.target}'.", false, null);
                }
            }
            else
            {
                throw new CommandFailureException("QA_MISSING_TARGET", "Either --qa-id or --target is required for qa click.", false, null);
            }

            string resolvedPath = GetGameObjectPath(target);
            ClickGameObject(target, ResolvePointerButton(args.button));

            return ProtocolJson.Serialize(new QaClickPayload
            {
                targetFound = true,
                resolvedPath = resolvedPath,
                qaId = resolvedQaId,
            });
        }

        private static string HandleTap(string argumentsJson)
        {
            QaTapArgs args = ProtocolJson.Deserialize<QaTapArgs>(argumentsJson) ?? new QaTapArgs();

            if (!string.IsNullOrWhiteSpace(args.target))
            {
                return HandleTapTarget(args);
            }

            Vector2Int screenPosition = ResolveTapScreenPosition(args);
            PointerEventData.InputButton pointerButton = ResolvePointerButton(args.button);

            if (pointerButton == PointerEventData.InputButton.Right)
            {
                return HandleRightCoordinateTap(screenPosition, args.button);
            }

            bool legacyInput = QaLegacyInputSimulator.IsLegacyOnly;
            if (legacyInput && EventSystem.current == null)
            {
                // Legacy Input Manager game without UI: deliver the tap as touch / left-mouse input.
                QaInputSimulator.SimulateTap(screenPosition, args.button);
                return ProtocolJson.Serialize(new QaTapPayload { completed = true });
            }

            EventSystem eventSystem = RequireEventSystem();
            var pointerData = new PointerEventData(eventSystem)
            {
                position = new Vector2(screenPosition.x, screenPosition.y),
                button = pointerButton,
            };

            var results = new List<RaycastResult>();
            eventSystem.RaycastAll(pointerData, results);

            if (results.Count == 0)
            {
                if (legacyInput)
                {
                    QaInputSimulator.SimulateTap(screenPosition, args.button);
                    return ProtocolJson.Serialize(new QaTapPayload { completed = true });
                }

#if ENABLE_INPUT_SYSTEM
                if (pointerButton == PointerEventData.InputButton.Right)
                {
                    QaInputSimulator.SimulateTap(screenPosition, args.button);
                    return ProtocolJson.Serialize(new QaTapPayload { completed = true });
                }
#endif
                throw new CommandFailureException(
                    "QA_TAP_NO_TARGET",
                    $"No UI element found at screen coordinates ({screenPosition.x}, {screenPosition.y}).",
                    false,
                    null);
            }

            GameObject rawTarget = results[0].gameObject;
            pointerData.pointerCurrentRaycast = results[0];

            GameObject clickTarget = ExecuteEvents.GetEventHandler<IPointerClickHandler>(rawTarget) ?? rawTarget;
            ClickGameObject(clickTarget, pointerButton);

            return ProtocolJson.Serialize(new QaTapPayload
            {
                completed = true,
            });
        }

        private static string HandleRightCoordinateTap(Vector2Int screenPosition, string? button)
        {
            EventSystem? eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                QaInputSimulator.SimulateTap(screenPosition, button);
                return ProtocolJson.Serialize(new QaTapPayload { completed = true });
            }

            var pointerData = new PointerEventData(eventSystem)
            {
                position = new Vector2(screenPosition.x, screenPosition.y),
                button = PointerEventData.InputButton.Right,
            };

            var results = new List<RaycastResult>();
            eventSystem.RaycastAll(pointerData, results);

            if (results.Count > 0)
            {
                GameObject rawTarget = results[0].gameObject;
                if (TryResolvePointerEventTarget(rawTarget, out GameObject pointerTarget))
                {
                    ClickGameObject(pointerTarget, PointerEventData.InputButton.Right);
                    return ProtocolJson.Serialize(new QaTapPayload { completed = true });
                }
            }

            QaInputSimulator.SimulateTap(screenPosition, button);
            return ProtocolJson.Serialize(new QaTapPayload { completed = true });
        }

        private static string HandleTapTarget(QaTapArgs args)
        {
            string target = args.target!;
            if (!QaTargetRegistry.TryResolvePath(target, out GameObject? gameObject) || gameObject == null)
            {
                throw new CommandFailureException("QA_TARGET_NOT_FOUND", $"No active GameObject found at path '{target}'.", false, null);
            }

            PointerEventData.InputButton pointerButton = ResolvePointerButton(args.button);
            if (pointerButton == PointerEventData.InputButton.Right
                && TryResolvePointerEventTarget(gameObject, out GameObject pointerTarget))
            {
                ClickGameObject(pointerTarget, pointerButton);
                return ProtocolJson.Serialize(new QaTapPayload { completed = true });
            }

            if (pointerButton == PointerEventData.InputButton.Left
                && TryInvokeQaTappable(gameObject))
            {
                return ProtocolJson.Serialize(new QaTapPayload { completed = true });
            }

            Vector2 anchorScreenPosition = GetWorldTapScreenPosition(gameObject, target);
            QaInputSimulator.SimulateTap(anchorScreenPosition, args.button);
            return ProtocolJson.Serialize(new QaTapPayload { completed = true });
        }

        private static bool TryInvokeQaTappable(GameObject gameObject)
        {
            foreach (IQaTappable tappable in gameObject.GetComponents<IQaTappable>())
            {
                if (tappable is Behaviour behaviour && !behaviour.isActiveAndEnabled)
                {
                    continue;
                }

                if (tappable.TryQaTap())
                {
                    return true;
                }
            }

            return false;
        }

        private static Vector2 GetWorldTapScreenPosition(GameObject gameObject, string target)
        {
            Transform anchor = ResolveTapAnchor(gameObject);
            Camera? camera = Camera.main;
            if (camera == null)
            {
                throw new CommandFailureException(
                    "QA_NO_CAMERA",
                    "qa tap --target requires a main camera to resolve a world object's screen position.",
                    false,
                    null);
            }

            Vector3 screenPoint = camera.WorldToScreenPoint(anchor.position);
            Vector2Int screenSize = GetGameViewRenderSize();
            if (screenPoint.z <= 0f
                || screenPoint.x < 0f || screenPoint.x > screenSize.x
                || screenPoint.y < 0f || screenPoint.y > screenSize.y)
            {
                throw new CommandFailureException(
                    "QA_TARGET_OFFSCREEN",
                    $"qa tap --target: '{target}' anchor is off-screen; nothing to tap.",
                    false,
                    null);
            }

            return new Vector2(screenPoint.x, screenPoint.y);
        }

        private static Transform ResolveTapAnchor(GameObject gameObject)
        {
            foreach (IQaTappable tappable in gameObject.GetComponents<IQaTappable>())
            {
                Transform? anchor = tappable.QaAnchor;
                if (anchor != null)
                {
                    return anchor;
                }
            }

            return gameObject.transform;
        }

        private static Vector2Int ResolveTapScreenPosition(QaTapArgs args)
        {
            Vector2Int screenSize = GetGameViewRenderSize();
            Vector2Int screenshotSize = ResolveScreenshotSize(args.screenshotWidth, args.screenshotHeight, screenSize);
            int screenshotWidth = screenshotSize.x;
            int screenshotHeight = screenshotSize.y;
            WarnIfAspectMismatch(screenshotWidth, screenshotHeight, screenSize.x, screenSize.y);
            int screenX = QaCoordinateConverter.ConvertScreenshotXToScreenX(args.x, screenSize.x, screenshotWidth);
            int screenY = QaCoordinateConverter.ConvertScreenshotYToScreenY(args.y, screenSize.y, screenshotHeight);
            return new Vector2Int(screenX, screenY);
        }

        private static Vector2Int ResolveScreenshotSize(int argWidth, int argHeight, Vector2Int gameViewSize)
        {
            int width = argWidth > 0
                ? argWidth
                : (ScreenshotCommandHandler.LastCapturedWidth > 0 ? ScreenshotCommandHandler.LastCapturedWidth : gameViewSize.x);
            int height = argHeight > 0
                ? argHeight
                : (ScreenshotCommandHandler.LastCapturedHeight > 0 ? ScreenshotCommandHandler.LastCapturedHeight : gameViewSize.y);
            return new Vector2Int(width, height);
        }

        private static string HandleUiDump(string argumentsJson)
        {
            QaUiDumpArgs args = ProtocolJson.Deserialize<QaUiDumpArgs>(argumentsJson) ?? new QaUiDumpArgs();
            Vector2Int screenSize = GetGameViewRenderSize();
            Vector2Int screenshotSize = ResolveScreenshotSize(args.screenshotWidth, args.screenshotHeight, screenSize);
            int screenshotWidth = screenshotSize.x;
            int screenshotHeight = screenshotSize.y;
            WarnIfAspectMismatch(screenshotWidth, screenshotHeight, screenSize.x, screenSize.y);

            List<QaUiElement> elements = CollectClickableUiElements(screenshotWidth, screenshotHeight, screenSize);
            elements.Sort(CompareUiElements);
            QaUiElement[] filteredElements = QaDumpProjectionUtility.ApplyUiDumpFilters(elements, args);
            if (args.omitRect)
            {
                return JsonConvert.SerializeObject(new
                {
                    elements = filteredElements.Select(element => new
                    {
                        element.path,
                        element.type,
                        element.text,
                        element.interactable,
                        element.centerX,
                        element.centerY,
                    }).ToArray(),
                }, BridgeJsonSettings.CamelCaseIgnoreNull);
            }

            return ProtocolJson.Serialize(new QaUiDumpPayload
            {
                elements = filteredElements,
            });
        }

        private static List<QaUiElement> CollectClickableUiElements(int screenshotWidth, int screenshotHeight, Vector2Int screenSize)
        {
            var elements = new List<QaUiElement>();
            var seenGameObjects = new HashSet<ulong>();

#if UNITY_2022_2_OR_NEWER
            MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
            MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
#endif
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (!IsClickableUiElementBehaviour(behaviour))
                {
                    continue;
                }

                GameObject gameObject = behaviour.gameObject;
                if (!seenGameObjects.Add(UnityObjectIdentity.GetId(gameObject)))
                {
                    continue;
                }

                elements.Add(CreateUiElement(gameObject, behaviour, screenshotWidth, screenshotHeight, screenSize));
            }

            return elements;
        }

        private static bool IsClickableUiElementBehaviour(MonoBehaviour? behaviour)
        {
            return behaviour != null
                && behaviour is IPointerClickHandler
                && behaviour.isActiveAndEnabled
                && behaviour.gameObject.activeInHierarchy
                && behaviour.gameObject.TryGetComponent<RectTransform>(out _);
        }

        private static bool IsClickableUiElement(GameObject gameObject)
        {
            MonoBehaviour[] behaviours = gameObject.GetComponents<MonoBehaviour>();
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (IsClickableUiElementBehaviour(behaviour))
                {
                    return true;
                }
            }

            return false;
        }

        private static QaUiElement CreateUiElement(
            GameObject gameObject,
            MonoBehaviour clickHandler,
            int screenshotWidth,
            int screenshotHeight,
            Vector2Int screenSize)
        {
            var element = new QaUiElement
            {
                path = GetGameObjectPath(gameObject),
                type = GetPointerClickHandlerTypeName(gameObject, clickHandler.GetType().Name),
                text = GetFirstTextValue(gameObject),
                interactable = GetInteractableValue(gameObject),
            };

            ScreenPositionContext context = GetScreenPositionContext(gameObject);
            RectTransform rectTransform = context.RectTransform
                ?? throw new InvalidOperationException("ui-dump elements must have a RectTransform.");
            ApplyRectTransformImageBounds(element, rectTransform, context, screenshotWidth, screenshotHeight, screenSize);

            return element;
        }

        private static string GetPointerClickHandlerTypeName(GameObject gameObject, string fallbackTypeName)
        {
            MonoBehaviour[] behaviours = gameObject.GetComponents<MonoBehaviour>();
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour != null && behaviour is IPointerClickHandler && behaviour.isActiveAndEnabled)
                {
                    return behaviour.GetType().Name;
                }
            }

            return fallbackTypeName;
        }

        internal static string GetFirstTextValue(GameObject gameObject)
        {
            if (TryGetFirstTextValueOnGameObject(gameObject, out string value))
            {
                return value;
            }

            foreach (Transform child in gameObject.transform)
            {
                if (TryGetFirstTextValueInOwnedSubtree(child.gameObject, out value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static bool TryGetFirstTextValueInOwnedSubtree(GameObject gameObject, out string value)
        {
            if (IsClickableUiElement(gameObject))
            {
                value = string.Empty;
                return false;
            }

            if (TryGetFirstTextValueOnGameObject(gameObject, out value))
            {
                return true;
            }

            foreach (Transform child in gameObject.transform)
            {
                if (TryGetFirstTextValueInOwnedSubtree(child.gameObject, out value))
                {
                    return true;
                }
            }

            value = string.Empty;
            return false;
        }

        private static bool TryGetFirstTextValueOnGameObject(GameObject gameObject, out string value)
        {
            Component[] components = gameObject.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component == null)
                {
                    continue;
                }

                PropertyInfo? property = component.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(string) || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                try
                {
                    if (property.GetValue(component) is string textValue && !string.IsNullOrEmpty(textValue))
                    {
                        value = textValue;
                        return true;
                    }
                }
                catch (Exception)
                {
                }
            }

            value = string.Empty;
            return false;
        }

        private static bool GetInteractableValue(GameObject gameObject)
        {
            Component[] components = gameObject.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component == null)
                {
                    continue;
                }

                MethodInfo? method = component.GetType().GetMethod(
                    "IsInteractable",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (method == null || method.ReturnType != typeof(bool))
                {
                    continue;
                }

                try
                {
                    if (method.Invoke(component, null) is bool value)
                    {
                        return value;
                    }
                }
                catch (Exception)
                {
                }
            }

            foreach (Component component in components)
            {
                if (component == null)
                {
                    continue;
                }

                PropertyInfo? property = component.GetType().GetProperty("interactable", BindingFlags.Instance | BindingFlags.Public);
                if (property == null || property.PropertyType != typeof(bool) || property.GetIndexParameters().Length != 0)
                {
                    continue;
                }

                try
                {
                    if (property.GetValue(component) is bool value)
                    {
                        return value;
                    }
                }
                catch (Exception)
                {
                }
            }

            return true;
        }

        private static void ApplyRectTransformImageBounds(
            QaUiElement element,
            RectTransform rectTransform,
            ScreenPositionContext context,
            int screenshotWidth,
            int screenshotHeight,
            Vector2Int screenSize)
        {
            var corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);

            float minX = float.MaxValue;
            float minY = float.MaxValue;
            float maxX = float.MinValue;
            float maxY = float.MinValue;

            for (int index = 0; index < corners.Length; index++)
            {
                Vector2 screenPoint = WorldToScreenPoint(corners[index], context);
                minX = Mathf.Min(minX, screenPoint.x);
                minY = Mathf.Min(minY, screenPoint.y);
                maxX = Mathf.Max(maxX, screenPoint.x);
                maxY = Mathf.Max(maxY, screenPoint.y);
            }

            int imgLeft = QaCoordinateConverter.ConvertScreenXToScreenshotX((int)minX, screenSize.x, screenshotWidth);
            int imgRight = QaCoordinateConverter.ConvertScreenXToScreenshotX((int)maxX, screenSize.x, screenshotWidth);
            int imgTop = QaCoordinateConverter.ConvertScreenYToScreenshotY((int)maxY, screenSize.y, screenshotHeight);
            int imgBottom = QaCoordinateConverter.ConvertScreenYToScreenshotY((int)minY, screenSize.y, screenshotHeight);

            element.x = imgLeft;
            element.y = imgTop;
            element.width = imgRight - imgLeft;
            element.height = imgBottom - imgTop;
            element.centerX = (imgLeft + imgRight) / 2;
            element.centerY = (imgTop + imgBottom) / 2;
        }

        private static int CompareUiElements(QaUiElement left, QaUiElement right)
        {
            int compare = left.centerY.CompareTo(right.centerY);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.centerX.CompareTo(right.centerX);
            if (compare != 0)
            {
                return compare;
            }

            return string.CompareOrdinal(left.path, right.path);
        }

        private static string HandleWorldDumpEditMode(string argumentsJson)
        {
            QaWorldDumpArgs args = ProtocolJson.Deserialize<QaWorldDumpArgs>(argumentsJson) ?? new QaWorldDumpArgs();
            List<QaWorldElement> elements = CollectEditModeWorldTappables();
            elements.Sort(CompareEditModeWorldElements);
            QaWorldElement[] filteredElements = QaDumpProjectionUtility.ApplyWorldDumpFilters(elements, args);
            string activeScenePath = UnityEditor.SceneManagement.EditorSceneManager.GetActiveScene().path;
            return JsonConvert.SerializeObject(new
            {
                editMode = true,
                hint = QaWorldDumpEditMode.Hint,
                activeScenePath = string.IsNullOrEmpty(activeScenePath) ? null : activeScenePath,
                elements = filteredElements.Select(element => new
                {
                    element.path,
                    element.label,
                    element.hasAction,
                }).ToArray(),
            }, BridgeJsonSettings.CamelCaseIgnoreNull);
        }

        private static List<QaWorldElement> CollectEditModeWorldTappables()
        {
            var elements = new List<QaWorldElement>();
            var seenGameObjects = new HashSet<ulong>();

#if UNITY_2022_2_OR_NEWER
            MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
            MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
#endif
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null
                    || behaviour is not IQaTappable tappable
                    || !behaviour.isActiveAndEnabled
                    || !behaviour.gameObject.activeInHierarchy)
                {
                    continue;
                }

                GameObject gameObject = behaviour.gameObject;
                if (!seenGameObjects.Add(UnityObjectIdentity.GetId(gameObject)))
                {
                    continue;
                }

                elements.Add(new QaWorldElement
                {
                    path = GetGameObjectPath(gameObject),
                    label = tappable.QaLabel,
                    hasAction = DetermineHasAction(tappable),
                });
            }

            return elements;
        }

        private static int CompareEditModeWorldElements(QaWorldElement left, QaWorldElement right)
        {
            return string.CompareOrdinal(left.path, right.path);
        }

        private static string HandleWorldDump(string argumentsJson)
        {
            QaWorldDumpArgs args = ProtocolJson.Deserialize<QaWorldDumpArgs>(argumentsJson) ?? new QaWorldDumpArgs();
            Vector2Int screenSize = GetGameViewRenderSize();
            Vector2Int screenshotSize = ResolveScreenshotSize(args.screenshotWidth, args.screenshotHeight, screenSize);
            int screenshotWidth = screenshotSize.x;
            int screenshotHeight = screenshotSize.y;
            WarnIfAspectMismatch(screenshotWidth, screenshotHeight, screenSize.x, screenSize.y);

            Camera? camera = Camera.main;
            if (camera == null)
            {
                throw new CommandFailureException(
                    "QA_NO_CAMERA",
                    "qa world-dump requires a main camera to project world objects to screen.",
                    false,
                    null);
            }

            List<QaWorldElement> elements = CollectWorldTappables(
                camera, args.includeOffscreen, screenshotWidth, screenshotHeight, screenSize);
            elements.Sort(CompareWorldElements);
            QaWorldElement[] filteredElements = QaDumpProjectionUtility.ApplyWorldDumpFilters(elements, args);
            bool includeOnScreen = QaDumpProjectionUtility.ShouldIncludeWorldOnScreenField(filteredElements, args);
            bool includeHasAction = QaDumpProjectionUtility.ShouldIncludeWorldHasActionField(filteredElements);
            bool useTrimProjection = args.limit > 0 || !string.IsNullOrWhiteSpace(args.text);
            if (useTrimProjection && (!includeOnScreen || !includeHasAction))
            {
                return JsonConvert.SerializeObject(new
                {
                    elements = filteredElements.Select(element => new
                    {
                        element.path,
                        element.label,
                        element.centerX,
                        element.centerY,
                        onScreen = includeOnScreen ? (bool?)element.onScreen : null,
                        hasAction = includeHasAction ? (bool?)element.hasAction : null,
                    }).ToArray(),
                }, BridgeJsonSettings.CamelCaseIgnoreNull);
            }

            return ProtocolJson.Serialize(new QaWorldDumpPayload
            {
                elements = filteredElements,
            });
        }

        private static List<QaWorldElement> CollectWorldTappables(
            Camera camera,
            bool includeOffscreen,
            int screenshotWidth,
            int screenshotHeight,
            Vector2Int screenSize)
        {
            var elements = new List<QaWorldElement>();
            var seenGameObjects = new HashSet<ulong>();

#if UNITY_2022_2_OR_NEWER
            MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
            MonoBehaviour[] behaviours = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
#endif
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null
                    || behaviour is not IQaTappable tappable
                    || !behaviour.isActiveAndEnabled
                    || !behaviour.gameObject.activeInHierarchy)
                {
                    continue;
                }

                GameObject gameObject = behaviour.gameObject;
                if (!seenGameObjects.Add(UnityObjectIdentity.GetId(gameObject)))
                {
                    continue;
                }

                QaWorldElement? element = CreateWorldElement(
                    gameObject, tappable, camera, includeOffscreen, screenshotWidth, screenshotHeight, screenSize);
                if (element != null)
                {
                    elements.Add(element);
                }
            }

            return elements;
        }

        private static QaWorldElement? CreateWorldElement(
            GameObject gameObject,
            IQaTappable tappable,
            Camera camera,
            bool includeOffscreen,
            int screenshotWidth,
            int screenshotHeight,
            Vector2Int screenSize)
        {
            Transform anchor = tappable.QaAnchor != null ? tappable.QaAnchor! : gameObject.transform;

            bool onScreen = false;
            int centerX = 0;
            int centerY = 0;

            Vector3 screenPoint = camera.WorldToScreenPoint(anchor.position);
            onScreen = screenPoint.z > 0f
                && screenPoint.x >= 0f && screenPoint.x <= screenSize.x
                && screenPoint.y >= 0f && screenPoint.y <= screenSize.y;
            centerX = QaCoordinateConverter.ConvertScreenXToScreenshotX((int)screenPoint.x, screenSize.x, screenshotWidth);
            centerY = QaCoordinateConverter.ConvertScreenYToScreenshotY((int)screenPoint.y, screenSize.y, screenshotHeight);

            if (!onScreen && !includeOffscreen)
            {
                return null;
            }

            return new QaWorldElement
            {
                path = GetGameObjectPath(gameObject),
                label = tappable.QaLabel,
                centerX = centerX,
                centerY = centerY,
                onScreen = onScreen,
                hasAction = DetermineHasAction(tappable),
            };
        }

        private static bool DetermineHasAction(IQaTappable tappable)
        {
            // QaTappable reports action availability without side effects via its persistent listener count;
            // custom IQaTappable implementations are optimistically reported as actionable (confirmed at tap time).
            if (tappable is QaTappable marker)
            {
                return marker.onQaTap != null && marker.onQaTap.GetPersistentEventCount() > 0;
            }

            return true;
        }

        private static int CompareWorldElements(QaWorldElement left, QaWorldElement right)
        {
            int compare = left.centerY.CompareTo(right.centerY);
            if (compare != 0)
            {
                return compare;
            }

            compare = left.centerX.CompareTo(right.centerX);
            if (compare != 0)
            {
                return compare;
            }

            return string.CompareOrdinal(left.path, right.path);
        }

        private static string HandleKey(string argumentsJson)
        {
            QaKeyArgs args = ProtocolJson.Deserialize<QaKeyArgs>(argumentsJson) ?? new QaKeyArgs();
            if (string.IsNullOrWhiteSpace(args.key))
            {
                throw new CommandFailureException("QA_MISSING_KEY", "--key is required for qa key.", false, null);
            }

            QaInputSimulator.SimulateKey(args.key);
            return ProtocolJson.Serialize(new QaKeyPayload
            {
                completed = true,
            });
        }

        private static void StartWaitUntilDeferred(
            string argumentsJson,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId)
        {
            QaWaitUntilArgs args = ProtocolJson.Deserialize<QaWaitUntilArgs>(argumentsJson) ?? new QaWaitUntilArgs();
            ValidateWaitUntilArgs(args);
            int timeoutMs = args.timeoutMs > 0 ? args.timeoutMs : ProtocolConstants.DefaultQaWaitUntilTimeoutMs;
            var stopwatch = Stopwatch.StartNew();
            var reasonSegments = new List<string>(3);

            void Poll()
            {
                if (completion.Task.IsCompleted)
                {
                    EditorTickPump.Remove(Poll);
                    return;
                }

                try
                {
                    EnsureDeferredPlayMode();

                    int elapsedMs = GetElapsedMilliseconds(stopwatch);

                    if (CheckCondition(args, reasonSegments, out string? reason))
                    {
                        CompleteSuccess(new QaWaitUntilPayload
                        {
                            conditionMet = true,
                            elapsedMs = elapsedMs,
                            reason = reason,
                        });
                        return;
                    }

                    if (elapsedMs >= timeoutMs)
                    {
                        EditorTickPump.Remove(Poll);
                        stopwatch.Stop();
                        completion.TrySetResult(ResponseEnvelope.Failure(
                            requestId,
                            projectHash,
                            "QA_WAIT_TIMEOUT",
                            "Timeout reached before condition was met.",
                            false,
                            stopwatch.ElapsedMilliseconds,
                            ProtocolConstants.TransportLive,
                            null));
                        return;
                    }
                }
                catch (Exception exception)
                {
                    CompleteFailure(exception);
                }
            }

            void CompleteSuccess(QaWaitUntilPayload payload)
            {
                EditorTickPump.Remove(Poll);
                stopwatch.Stop();
                completion.TrySetResult(CreateSuccessResponse(requestId, projectHash, payload, stopwatch.ElapsedMilliseconds));
            }

            void CompleteFailure(Exception exception)
            {
                EditorTickPump.Remove(Poll);
                stopwatch.Stop();
                completion.TrySetResult(CreateFailureResponse(requestId, projectHash, exception, stopwatch.ElapsedMilliseconds));
            }

            EditorTickPump.Add(Poll);
            Poll();
        }

        private static void StartSwipeDeferred(
            string argumentsJson,
            TaskCompletionSource<ResponseEnvelope> completion,
            string projectHash,
            string requestId)
        {
            QaSwipeArgs args = ProtocolJson.Deserialize<QaSwipeArgs>(argumentsJson) ?? new QaSwipeArgs();
            SwipeScreenPositions swipeScreenPositions = ResolveSwipeScreenPositions(args);
            IQaSwipeOperation swipe = QaInputSimulator.BeginSwipe(
                swipeScreenPositions.FromScreenPosition,
                swipeScreenPositions.ToScreenPosition,
                args.durationMs,
                args.button);
            var stopwatch = Stopwatch.StartNew();

            void Poll()
            {
                if (completion.Task.IsCompleted)
                {
                    EditorTickPump.Remove(Poll);
                    swipe.Abort();
                    return;
                }

                try
                {
                    EnsureDeferredPlayMode();

                    if (swipe.Advance())
                    {
                        EditorTickPump.Remove(Poll);
                        stopwatch.Stop();
                        completion.TrySetResult(CreateSuccessResponse(
                            requestId,
                            projectHash,
                            new QaSwipePayload
                            {
                                completed = true,
                            },
                            stopwatch.ElapsedMilliseconds));
                    }
                }
                catch (Exception exception)
                {
                    EditorTickPump.Remove(Poll);
                    swipe.Abort();
                    stopwatch.Stop();
                    completion.TrySetResult(CreateFailureResponse(requestId, projectHash, exception, stopwatch.ElapsedMilliseconds));
                }
            }

            EditorTickPump.Add(Poll);
            Poll();
        }

        private static void ValidateWaitUntilArgs(QaWaitUntilArgs args)
        {
            bool hasCondition = !string.IsNullOrWhiteSpace(args.scene)
                || !string.IsNullOrWhiteSpace(args.logContains)
                || !string.IsNullOrWhiteSpace(args.objectExists)
                || !string.IsNullOrWhiteSpace(args.objectInteractable)
                || !string.IsNullOrWhiteSpace(args.objectGone);

            if (!hasCondition)
            {
                throw new CommandFailureException(
                    "QA_MISSING_CONDITION",
                    "At least one condition (--scene, --log-contains, --object-exists, --object-interactable, --object-gone) is required.",
                    false,
                    null);
            }
        }

        private static void EnsureDeferredPlayMode()
        {
            if (!EditorApplication.isPlaying)
            {
                throw new CommandFailureException("QA_NOT_PLAYING", "QA commands require Play Mode.", false, null);
            }
        }

        private static string GetRequestId(TaskCompletionSource<ResponseEnvelope> completion)
        {
            string? requestId = completion.Task.AsyncState as string;
            if (string.IsNullOrWhiteSpace(requestId))
            {
                throw new InvalidOperationException("Deferred QA request ID is missing.");
            }

            return requestId;
        }

        private static int GetElapsedMilliseconds(Stopwatch stopwatch)
        {
            return stopwatch.ElapsedMilliseconds >= int.MaxValue
                ? int.MaxValue
                : (int)stopwatch.ElapsedMilliseconds;
        }

        private static ResponseEnvelope CreateSuccessResponse(string requestId, string projectHash, object payload, long durationMs)
        {
            return ResponseEnvelope.Success(
                requestId,
                projectHash,
                ProtocolJson.Serialize(payload),
                durationMs,
                ProtocolConstants.TransportLive);
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

        private static string HandleSwipeOnTarget(string argumentsJson)
        {
            QaSwipeArgs args = ProtocolJson.Deserialize<QaSwipeArgs>(argumentsJson) ?? new QaSwipeArgs();
            GameObject target = ResolveSwipeTarget(args);
            SwipeScreenPositions swipeScreenPositions = ResolveTargetSwipeScreenPositions(target, args);
            int steps = Mathf.Max(1, Mathf.CeilToInt(args.durationMs / 16f));

            DragGameObject(target, swipeScreenPositions.FromScreenPosition, swipeScreenPositions.ToScreenPosition, steps, args.button);

            return ProtocolJson.Serialize(new QaSwipePayload
            {
                completed = true,
            });
        }

        private static SwipeScreenPositions ResolveSwipeScreenPositions(QaSwipeArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.target))
            {
                Vector2Int screenSize = GetGameViewRenderSize();
                Vector2Int screenshotSize = ResolveScreenshotSize(args.screenshotWidth, args.screenshotHeight, screenSize);
                int screenshotWidth = screenshotSize.x;
                int screenshotHeight = screenshotSize.y;
                WarnIfAspectMismatch(screenshotWidth, screenshotHeight, screenSize.x, screenSize.y);
                return new SwipeScreenPositions(
                    ConvertRawSwipeCoordinateToScreenPosition(args.fromX, args.fromY, screenshotWidth, screenshotHeight, screenSize),
                    ConvertRawSwipeCoordinateToScreenPosition(args.toX, args.toY, screenshotWidth, screenshotHeight, screenSize));
            }

            GameObject target = ResolveSwipeTarget(args);
            return ResolveTargetSwipeScreenPositions(target, args);
        }

        private static Vector2 ConvertRawSwipeCoordinateToScreenPosition(int rawX, int rawY, int screenshotWidth, int screenshotHeight, Vector2Int screenSize)
        {
            int screenX = QaCoordinateConverter.ConvertScreenshotXToScreenX(rawX, screenSize.x, screenshotWidth);
            int screenY = QaCoordinateConverter.ConvertScreenshotYToScreenY(rawY, screenSize.y, screenshotHeight);
            return new Vector2(screenX, screenY);
        }

        private static GameObject ResolveSwipeTarget(QaSwipeArgs args)
        {
            if (string.IsNullOrWhiteSpace(args.target))
            {
                throw new CommandFailureException("QA_MISSING_TARGET", "qa swipe --target requires a target path.", false, null);
            }

            if (!QaTargetRegistry.TryResolvePath(args.target, out GameObject? target) || target == null)
            {
                throw new CommandFailureException("QA_TARGET_NOT_FOUND", $"No active GameObject found at path '{args.target}'.", false, null);
            }

            return target;
        }

        private static SwipeScreenPositions ResolveTargetSwipeScreenPositions(GameObject target, QaSwipeArgs args)
        {
            ScreenPositionContext context = GetScreenPositionContext(target);
            RectTransform? rectTransform = context.RectTransform;
            if (rectTransform == null)
            {
                throw new CommandFailureException(
                    "QA_TARGET_NOT_RECT_TRANSFORM",
                    $"qa swipe --target requires a RectTransform target. '{args.target}' does not have one.",
                    false,
                    null);
            }

            Vector2 targetCenterScreenPosition = GetScreenCenterPosition(rectTransform, context);
            return new SwipeScreenPositions(
                targetCenterScreenPosition + new Vector2(args.fromX, args.fromY),
                targetCenterScreenPosition + new Vector2(args.toX, args.toY));
        }

        private static void DragGameObject(GameObject target, Vector2 from, Vector2 to, int steps, string? button)
        {
            EventSystem eventSystem = RequireEventSystem();
            var pointerData = new PointerEventData(eventSystem)
            {
                position = from,
                pressPosition = from,
                button = ResolvePointerButton(button),
                pointerDrag = target,
            };

            ExecuteEvents.Execute(target, pointerData, ExecuteEvents.pointerDownHandler);
            ExecuteEvents.Execute(target, pointerData, ExecuteEvents.beginDragHandler);

            for (int i = 1; i <= steps; i++)
            {
                float t = (float)i / steps;
                Vector2 position = Vector2.Lerp(from, to, t);
                pointerData.position = position;
                pointerData.delta = position - Vector2.Lerp(from, to, (float)(i - 1) / steps);
                ExecuteEvents.Execute(target, pointerData, ExecuteEvents.dragHandler);
            }

            pointerData.position = to;
            ExecuteEvents.Execute(target, pointerData, ExecuteEvents.endDragHandler);
            ExecuteEvents.Execute(target, pointerData, ExecuteEvents.pointerUpHandler);
        }

        private static void ClickGameObject(GameObject target, PointerEventData.InputButton button)
        {
            EventSystem eventSystem = RequireEventSystem();
            var pointerData = new PointerEventData(eventSystem)
            {
                position = GetScreenPosition(target),
                button = button,
            };

            if (button == PointerEventData.InputButton.Right)
            {
                ExecuteEvents.Execute(target, pointerData, ExecuteEvents.pointerDownHandler);
                ExecuteEvents.Execute(target, pointerData, ExecuteEvents.pointerUpHandler);
            }

            ExecuteEvents.Execute(target, pointerData, ExecuteEvents.pointerClickHandler);
        }

        private static PointerEventData.InputButton ResolvePointerButton(string? button)
        {
            return string.Equals(button, "right", StringComparison.OrdinalIgnoreCase)
                ? PointerEventData.InputButton.Right
                : PointerEventData.InputButton.Left;
        }

        private static bool TryResolvePointerEventTarget(GameObject gameObject, out GameObject target)
        {
            target = ExecuteEvents.GetEventHandler<IPointerClickHandler>(gameObject)
                ?? ExecuteEvents.GetEventHandler<IPointerDownHandler>(gameObject)
                ?? ExecuteEvents.GetEventHandler<IPointerUpHandler>(gameObject)
                ?? gameObject;

            return ExecuteEvents.CanHandleEvent<IPointerClickHandler>(target)
                || ExecuteEvents.CanHandleEvent<IPointerDownHandler>(target)
                || ExecuteEvents.CanHandleEvent<IPointerUpHandler>(target);
        }

        private static EventSystem RequireEventSystem()
        {
            EventSystem? eventSystem = EventSystem.current;
            if (eventSystem == null)
            {
                throw new CommandFailureException("QA_NO_EVENT_SYSTEM", "An active EventSystem is required for QA pointer commands.", false, null);
            }

            return eventSystem;
        }

        private static bool CheckCondition(QaWaitUntilArgs args, List<string> reasonSegments, out string? reason)
        {
            reason = null;
            reasonSegments.Clear();

            if (!string.IsNullOrWhiteSpace(args.scene))
            {
                Scene activeScene = SceneManager.GetActiveScene();
                if (!string.Equals(activeScene.name, args.scene, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                reasonSegments.Add($"Active scene is '{activeScene.name}'.");
            }

            if (!string.IsNullOrWhiteSpace(args.logContains))
            {
                ConsoleLogEntry[] entries = ConsoleLogBuffer.Read(100, string.Empty);
                bool found = false;
                foreach (ConsoleLogEntry entry in entries)
                {
                    if (entry.message.IndexOf(args.logContains!, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        found = true;
                        break;
                    }
                }

                if (!found)
                {
                    return false;
                }

                reasonSegments.Add($"Log contains '{args.logContains}'.");
            }

            if (!string.IsNullOrWhiteSpace(args.objectExists))
            {
                GameObject? target;
                if ((!QaTargetRegistry.TryResolve(args.objectExists!, out target) || target == null)
                    && (!QaTargetRegistry.TryResolvePath(args.objectExists!, out target) || target == null))
                {
                    return false;
                }

                reasonSegments.Add($"Object '{args.objectExists}' exists.");
            }

            if (!string.IsNullOrWhiteSpace(args.objectInteractable))
            {
                GameObject? target;
                if ((!QaTargetRegistry.TryResolve(args.objectInteractable!, out target) || target == null)
                    && (!QaTargetRegistry.TryResolvePath(args.objectInteractable!, out target) || target == null))
                {
                    return false;
                }

                if (!GetInteractableValue(target))
                {
                    return false;
                }

                reasonSegments.Add($"Object '{args.objectInteractable}' is interactable.");
            }

            if (!string.IsNullOrWhiteSpace(args.objectGone))
            {
                GameObject? goneTarget;
                bool found = (QaTargetRegistry.TryResolve(args.objectGone!, out goneTarget) && goneTarget != null)
                    || (QaTargetRegistry.TryResolvePath(args.objectGone!, out goneTarget) && goneTarget != null);
                if (found)
                {
                    return false;
                }

                reasonSegments.Add($"Object '{args.objectGone}' is gone.");
            }

            reason = reasonSegments.Count > 0 ? string.Join(" ", reasonSegments) : null;
            return true;
        }

        private static Vector2 GetScreenPosition(GameObject gameObject)
        {
            ScreenPositionContext context = GetScreenPositionContext(gameObject);
            RectTransform? rectTransform = context.RectTransform;
            if (rectTransform != null)
            {
                return GetScreenCenterPosition(rectTransform, context);
            }

            Camera? mainCamera = Camera.main;
            if (mainCamera != null)
            {
                Vector3 screenPoint = mainCamera.WorldToScreenPoint(gameObject.transform.position);
                return new Vector2(screenPoint.x, screenPoint.y);
            }

            return Vector2.zero;
        }

        private static Vector2 GetScreenCenterPosition(
            RectTransform rectTransform,
            ScreenPositionContext context)
        {
            var corners = new Vector3[4];
            rectTransform.GetWorldCorners(corners);
            Vector3 worldPoint = (corners[0] + corners[2]) * 0.5f;
            return WorldToScreenPoint(worldPoint, context);
        }

        private static Vector2 WorldToScreenPoint(Vector3 worldPoint, ScreenPositionContext context)
        {
            Canvas? canvas = context.ParentCanvas;
            if (canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay)
            {
                Camera? canvasCamera = context.CanvasCamera;
                if (canvasCamera != null)
                {
                    return RectTransformUtility.WorldToScreenPoint(canvasCamera, worldPoint);
                }
            }

            return new Vector2(worldPoint.x, worldPoint.y);
        }

        private static void EnsureScreenPositionCacheSubscribed()
        {
            if (_isScreenPositionCacheSubscribed)
            {
                return;
            }

            _isScreenPositionCacheSubscribed = true;
            EditorApplication.hierarchyChanged += ClearScreenPositionCache;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static ScreenPositionContext GetScreenPositionContext(GameObject gameObject)
        {
            ulong instanceId = UnityObjectIdentity.GetId(gameObject);
            if (_screenPositionContextCache.TryGetValue(instanceId, out ScreenPositionContext? context))
            {
                return context;
            }

            gameObject.TryGetComponent(out RectTransform? rectTransform);
            Canvas? parentCanvas = rectTransform != null
                ? gameObject.GetComponentInParent<Canvas>()
                : null;
            Camera? canvasCamera = parentCanvas != null && parentCanvas.renderMode != RenderMode.ScreenSpaceOverlay
                ? parentCanvas.worldCamera ?? Camera.main
                : null;

            context = new ScreenPositionContext(rectTransform, parentCanvas, canvasCamera);
            _screenPositionContextCache[instanceId] = context;
            return context;
        }

        private static void ClearScreenPositionCache()
        {
            _screenPositionContextCache.Clear();
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            ClearScreenPositionCache();
        }

        private static string GetGameObjectPath(GameObject gameObject)
        {
            string path = gameObject.name;
            Transform? parent = gameObject.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }

            return "/" + path;
        }

        private sealed class ScreenPositionContext
        {
            public ScreenPositionContext(RectTransform? rectTransform, Canvas? parentCanvas, Camera? canvasCamera)
            {
                RectTransform = rectTransform;
                ParentCanvas = parentCanvas;
                CanvasCamera = canvasCamera;
            }

            public RectTransform? RectTransform { get; }

            public Canvas? ParentCanvas { get; }

            public Camera? CanvasCamera { get; }
        }

        private readonly struct SwipeScreenPositions
        {
            public SwipeScreenPositions(Vector2 fromScreenPosition, Vector2 toScreenPosition)
            {
                FromScreenPosition = fromScreenPosition;
                ToScreenPosition = toScreenPosition;
            }

            public Vector2 FromScreenPosition { get; }

            public Vector2 ToScreenPosition { get; }
        }
    }
}
