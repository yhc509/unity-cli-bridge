#nullable enable
using System;
using System.IO;
using System.Reflection;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    internal sealed class ScreenshotCommandHandler
    {
        private const string FormatJpg = ScreenshotDefaults.FormatJpg;

        internal static int LastCapturedWidth { get; private set; }
        internal static int LastCapturedHeight { get; private set; }

        [InitializeOnLoadMethod]
        private static void EnsurePlayModeStateChangedSubscribed()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        internal static void ResetLastCapturedSize()
        {
            LastCapturedWidth = 0;
            LastCapturedHeight = 0;
        }

        internal static void SetLastCapturedSizeForTesting(int width, int height)
        {
            LastCapturedWidth = width;
            LastCapturedHeight = height;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode
                || state == PlayModeStateChange.EnteredPlayMode)
            {
                ResetLastCapturedSize();
            }
        }

        public bool CanHandle(string command)
        {
            return string.Equals(command, ProtocolConstants.CommandScreenshot, StringComparison.Ordinal);
        }

        public string Handle(string command, string argumentsJson)
        {
            ScreenshotArgs args = ProtocolJson.Deserialize<ScreenshotArgs>(argumentsJson) ?? new ScreenshotArgs();

            string outputPath;
            int capturedWidth;
            int capturedHeight;
            string format = NormalizeScreenshotFormat(args.format, args.outputPath);
            int jpegQuality = NormalizeJpegQuality(args.quality);

            // Resolved once here so every capture path shares one answer; the explicit-size gate
            // that used to live at each call site is folded into ResolveMaxWidth.
            int maxWidth = ScreenshotDefaults.ResolveMaxWidth(args.maxWidth, args.width, args.height);

            if (!string.IsNullOrWhiteSpace(args.camera))
            {
                var result = CaptureFromCamera(args.camera!, args.width, args.height, maxWidth, format, jpegQuality);
                outputPath = result.path;
                capturedWidth = result.width;
                capturedHeight = result.height;
            }
            else
            {
                string view = string.IsNullOrWhiteSpace(args.view) ? "game" : args.view!;
                var result = CaptureView(view, args.width, args.height, maxWidth, format, jpegQuality);
                outputPath = result.path;
                capturedWidth = result.width;
                capturedHeight = result.height;
            }

            string resolvedPath = !string.IsNullOrWhiteSpace(args.outputPath) ? args.outputPath! : outputPath;

            if (!string.Equals(outputPath, resolvedPath, StringComparison.Ordinal))
            {
                string? directory = Path.GetDirectoryName(resolvedPath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory!);
                }

                if (File.Exists(resolvedPath))
                {
                    File.Delete(resolvedPath);
                }

                File.Move(outputPath, resolvedPath);
            }

            var fileInfo = new FileInfo(resolvedPath);
            Vector2 gameViewSize = Handles.GetMainGameViewSize();
            LastCapturedWidth = capturedWidth;
            LastCapturedHeight = capturedHeight;

            return ProtocolJson.Serialize(new ScreenshotPayload
            {
                savedPath = resolvedPath,
                width = capturedWidth,
                height = capturedHeight,
                screenWidth = (int)gameViewSize.x,
                screenHeight = (int)gameViewSize.y,
                coordinateOrigin = "bottom-left",
                imageOrigin = "top-left",
                fileSizeBytes = fileInfo.Exists ? fileInfo.Length : 0,
                format = format,
            });
        }

        private (string path, int width, int height) CaptureView(
            string view,
            int requestedWidth,
            int requestedHeight,
            int maxWidth,
            string format,
            int jpegQuality)
        {
            string tempPath = CreateTempScreenshotPath(format);

            if (string.Equals(view, "game", StringComparison.OrdinalIgnoreCase))
            {
                return CaptureGameView(tempPath, requestedWidth, requestedHeight, maxWidth, format, jpegQuality);
            }

            if (string.Equals(view, "scene", StringComparison.OrdinalIgnoreCase))
            {
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView == null)
                {
                    throw new CommandFailureException("SCREENSHOT_FAILED", "Scene View is not open.", false, null);
                }

                int width = requestedWidth > 0 ? requestedWidth : (int)sceneView.position.width;
                int height = requestedHeight > 0 ? requestedHeight : (int)sceneView.position.height;

                Camera? camera = sceneView.camera;
                if (camera == null)
                {
                    throw new CommandFailureException("SCREENSHOT_FAILED", "No camera is available for a Scene View capture.", false, null);
                }

                var result = CaptureCameraToPath(camera, width, height, maxWidth, format, jpegQuality, tempPath);
                return (tempPath, result.width, result.height);
            }

            throw new CommandFailureException("INVALID_VIEW", $"Unsupported view: {view}", false, null);
        }

        private (string path, int width, int height) CaptureFromCamera(
            string cameraName,
            int requestedWidth,
            int requestedHeight,
            int maxWidth,
            string format,
            int jpegQuality)
        {
            var camera = FindCamera(cameraName);
            if (camera == null)
            {
                throw new CommandFailureException("CAMERA_NOT_FOUND", $"Could not find camera: {cameraName}", false, null);
            }

            int width = requestedWidth > 0 ? requestedWidth : camera.pixelWidth;
            int height = requestedHeight > 0 ? requestedHeight : camera.pixelHeight;
            var gameView = GetMainGameViewSize();
            if (width <= 0)
            {
                width = gameView.width;
            }

            if (height <= 0)
            {
                height = gameView.height;
            }

            string tempPath = CreateTempScreenshotPath(format);
            var result = CaptureCameraToPath(camera, width, height, maxWidth, format, jpegQuality, tempPath);
            return (tempPath, result.width, result.height);
        }

        private static (string path, int width, int height) CaptureGameView(
            string path,
            int requestedWidth,
            int requestedHeight,
            int maxWidth,
            string format,
            int jpegQuality)
        {
            if (!EditorApplication.isPlaying)
            {
                return CaptureGameViewFromCamera(path, requestedWidth, requestedHeight, maxWidth, format, jpegQuality);
            }

            Texture2D? capturedTexture = null;
            Texture2D? outputTexture = null;
            EditorWindow? gameViewWindow = null;

            try
            {
                gameViewWindow = GetGameViewWindow(focus: true);
                if (gameViewWindow == null)
                {
                    throw new CommandFailureException("SCREENSHOT_FAILED", "Could not find the Play Mode Game View window.", false, null);
                }

                capturedTexture = TryCaptureGameViewRenderTexture(gameViewWindow)
                    ?? ScreenCapture.CaptureScreenshotAsTexture();
                if (capturedTexture == null)
                {
                    throw new CommandFailureException("SCREENSHOT_FAILED", "Play Mode Game View capture failed.", false, null);
                }

                WarnIfSuspiciousPlayModeCapture(capturedTexture, gameViewWindow);

                var outputSize = ResolvePlayModeGameViewOutputSize(capturedTexture, requestedWidth, requestedHeight, maxWidth);
                int width = outputSize.width;
                int height = outputSize.height;

                outputTexture = outputSize.shouldResize
                    ? ResizeTexture(capturedTexture, width, height)
                    : capturedTexture;

                WriteTextureToPath(outputTexture, path, format, jpegQuality);
                return (path, width, height);
            }
            finally
            {
                if (outputTexture != null && !ReferenceEquals(outputTexture, capturedTexture))
                {
                    UnityEngine.Object.DestroyImmediate(outputTexture);
                }

                if (capturedTexture != null)
                {
                    UnityEngine.Object.DestroyImmediate(capturedTexture);
                }
            }
        }

        private static (int width, int height, bool shouldResize) ResolvePlayModeGameViewOutputSize(
            Texture2D capturedTexture,
            int requestedWidth,
            int requestedHeight,
            int maxWidth)
        {
            int width = requestedWidth > 0 ? requestedWidth : capturedTexture.width;
            int height = requestedHeight > 0 ? requestedHeight : capturedTexture.height;
            if (width <= 0 || height <= 0)
            {
                throw new CommandFailureException("SCREENSHOT_FAILED", "Could not determine a valid Game View capture size.", false, null);
            }

            if (width > capturedTexture.width || height > capturedTexture.height)
            {
                UnityEngine.Debug.LogWarning(
                    $"[UnityCliBridge] Play Mode Game View screenshot requested {width}x{height}, but the capture only returned the native Game View size {capturedTexture.width}x{capturedTexture.height}. Saving the native capture without upscaling.");
                return (capturedTexture.width, capturedTexture.height, false);
            }

            if (maxWidth > 0 && capturedTexture.width > maxWidth)
            {
                return (maxWidth, CalculateAspectFitHeight(capturedTexture.width, capturedTexture.height, maxWidth), true);
            }

            return (width, height, width != capturedTexture.width || height != capturedTexture.height);
        }

        private static void WarnIfSuspiciousPlayModeCapture(Texture2D capturedTexture, EditorWindow gameViewWindow)
        {
            if (capturedTexture.height >= 100)
            {
                return;
            }

            UnityEngine.Debug.LogWarning(
                $"[UnityCliBridge] Play Mode Game View screenshot produced an unusually small image {capturedTexture.width}x{capturedTexture.height}. Game View focus may not have settled before capture. Current Game View window size is {(int)gameViewWindow.position.width}x{(int)gameViewWindow.position.height}.");
        }

        private static Texture2D? TryCaptureGameViewRenderTexture(EditorWindow gameViewWindow)
        {
            try
            {
                // Read the Game View render target directly so Play Mode captures exclude editor chrome.
                RenderTexture? renderTexture = GetGameViewRenderTexture(gameViewWindow);
                if (renderTexture == null || !renderTexture.IsCreated() || renderTexture.width <= 0 || renderTexture.height <= 0)
                {
                    return null;
                }

                RenderTexture? previousActive = RenderTexture.active;
                Texture2D? texture = null;

                try
                {
                    RenderTexture.active = renderTexture;
                    texture = new Texture2D(renderTexture.width, renderTexture.height, TextureFormat.RGBA32, false);
                    texture.ReadPixels(new Rect(0, 0, renderTexture.width, renderTexture.height), 0, 0);
                    FlipTextureVertically(texture);
                    texture.Apply();
                    return texture;
                }
                catch (Exception exception)
                {
                    if (texture != null)
                    {
                        UnityEngine.Object.DestroyImmediate(texture);
                    }

                    UnityEngine.Debug.LogWarning(
                        $"[UnityCliBridge] Failed to capture the Game View render texture directly. Falling back to ScreenCapture.CaptureScreenshotAsTexture(). {exception.GetType().Name}: {exception.Message}");
                    return null;
                }
                finally
                {
                    RenderTexture.active = previousActive;
                }
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogWarning(
                    $"[UnityCliBridge] Failed to inspect the Game View render texture. Falling back to ScreenCapture.CaptureScreenshotAsTexture(). {exception.GetType().Name}: {exception.Message}");
                return null;
            }
        }

        private static void FlipTextureVertically(Texture2D texture)
        {
            int width = texture.width;
            int height = texture.height;
            Color32[] pixels = texture.GetPixels32();
            var flippedPixels = new Color32[pixels.Length];

            for (int y = 0; y < height; y++)
            {
                Array.Copy(pixels, y * width, flippedPixels, (height - y - 1) * width, width);
            }

            texture.SetPixels32(flippedPixels);
        }

        private static RenderTexture? GetGameViewRenderTexture(EditorWindow gameViewWindow)
        {
            string[] memberNames =
            {
                "m_TargetTexture",
                "m_RenderTexture",
                "targetTexture",
            };

            foreach (string memberName in memberNames)
            {
                if (GetMemberValue(gameViewWindow, memberName) is RenderTexture renderTexture)
                {
                    return renderTexture;
                }
            }

            return null;
        }

        private static (string path, int width, int height) CaptureGameViewFromCamera(
            string path,
            int requestedWidth,
            int requestedHeight,
            int maxWidth,
            string format,
            int jpegQuality)
        {
            Camera? camera = Camera.main;
            if (camera == null && Camera.allCameras.Length > 0)
            {
                camera = Camera.allCameras[0];
            }

            if (camera == null)
            {
                throw new CommandFailureException("SCREENSHOT_FAILED", "No camera is available for a Game View capture.", false, null);
            }

            var gameView = GetMainGameViewSize();
            int width = requestedWidth > 0 ? requestedWidth : camera.pixelWidth;
            int height = requestedHeight > 0 ? requestedHeight : camera.pixelHeight;
            if (width <= 0)
            {
                width = gameView.width;
            }

            if (height <= 0)
            {
                height = gameView.height;
            }

            var result = CaptureCameraToPath(camera, width, height, maxWidth, format, jpegQuality, path);
            return (path, result.width, result.height);
        }

        private static (int width, int height) CaptureCameraToPath(
            Camera camera,
            int width,
            int height,
            int maxWidth,
            string format,
            int jpegQuality,
            string path)
        {
            var renderTexture = new RenderTexture(width, height, 24);
            RenderTexture? previousActive = RenderTexture.active;
            RenderTexture? previousTarget = camera.targetTexture;
            Texture2D? texture = null;
            Texture2D? outputTexture = null;

            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();

                RenderTexture.active = renderTexture;
                texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();
                var output = ApplyMaxWidth(texture, maxWidth);
                outputTexture = output.texture;
                WriteTextureToPath(outputTexture, path, format, jpegQuality);
                return (output.width, output.height);
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;

                if (outputTexture != null && !ReferenceEquals(outputTexture, texture))
                {
                    UnityEngine.Object.DestroyImmediate(outputTexture);
                }

                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }

                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        private static Texture2D ResizeTexture(Texture2D sourceTexture, int width, int height)
        {
            var renderTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture? previousActive = RenderTexture.active;
            Texture2D? resizedTexture = null;

            try
            {
                Graphics.Blit(sourceTexture, renderTexture);
                RenderTexture.active = renderTexture;

                resizedTexture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                resizedTexture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                resizedTexture.Apply();
                return resizedTexture;
            }
            catch
            {
                if (resizedTexture != null)
                {
                    UnityEngine.Object.DestroyImmediate(resizedTexture);
                }

                throw;
            }
            finally
            {
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(renderTexture);
            }
        }

        private static (Texture2D texture, int width, int height) ApplyMaxWidth(Texture2D sourceTexture, int maxWidth)
        {
            if (maxWidth > 0 && sourceTexture.width > maxWidth)
            {
                int height = CalculateAspectFitHeight(sourceTexture.width, sourceTexture.height, maxWidth);
                Texture2D resizedTexture = ResizeTexture(sourceTexture, maxWidth, height);
                return (resizedTexture, maxWidth, height);
            }

            return (sourceTexture, sourceTexture.width, sourceTexture.height);
        }

        private static int CalculateAspectFitHeight(int width, int height, int maxWidth)
        {
            return Mathf.Max(1, Mathf.RoundToInt(height * maxWidth / (float)width));
        }

        private static string NormalizeScreenshotFormat(string? format, string? outputPath)
        {
            if (ScreenshotDefaults.TryResolveFormat(format, outputPath, out string resolved))
            {
                return resolved;
            }

            throw new CommandFailureException(
                "SCREENSHOT_INVALID_FORMAT",
                "screenshot format must be png, jpg, or jpeg.",
                false,
                null);
        }

        private static int NormalizeJpegQuality(int quality)
        {
            if (quality == 0)
            {
                return ScreenshotDefaults.DefaultJpegQuality;
            }

            if (quality >= 1 && quality <= 100)
            {
                return quality;
            }

            throw new CommandFailureException(
                "SCREENSHOT_INVALID_QUALITY",
                "screenshot quality must be from 1 to 100.",
                false,
                null);
        }

        private static string CreateTempScreenshotPath(string format)
        {
            return Path.Combine(
                Path.GetTempPath(),
                $"puc-screenshot-{Guid.NewGuid():N}{ScreenshotDefaults.FileExtension(format)}");
        }

        private static void WriteTextureToPath(Texture2D texture, string path, string format, int jpegQuality)
        {
            byte[] bytes = string.Equals(format, FormatJpg, StringComparison.Ordinal)
                ? texture.EncodeToJPG(jpegQuality)
                : texture.EncodeToPNG();
            File.WriteAllBytes(path, bytes);
        }

        private static Camera? FindCamera(string name)
        {
            foreach (var camera in Camera.allCameras)
            {
                if (string.Equals(camera.name, name, StringComparison.OrdinalIgnoreCase))
                {
                    return camera;
                }
            }

            return null;
        }

        private static Type? GetGameViewType()
        {
            return Type.GetType("UnityEditor.GameView,UnityEditor")
                ?? typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.GameView");
        }

        private static EditorWindow? GetGameViewWindow(bool focus)
        {
            Type? gameViewType = GetGameViewType();
            if (gameViewType == null)
            {
                return null;
            }

            EditorWindow? window = focus
                ? EditorWindow.GetWindow(gameViewType)
                : EditorWindow.GetWindow(gameViewType, false, null, false);

            if (window != null && focus)
            {
                window.Focus();
                window.Repaint();
            }

            return window;
        }

        private static object? GetMemberValue(object target, string memberName)
        {
            Type? type = target.GetType();
            while (type != null)
            {
                FieldInfo? field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                {
                    return field.GetValue(target);
                }

                PropertyInfo? property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(target, null);
                }

                type = type.BaseType;
            }

            return null;
        }

        private static (int width, int height) GetMainGameViewSize()
        {
            EditorWindow? window = GetGameViewWindow(focus: false);
            if (window == null)
            {
                return (1920, 1080);
            }

            return ((int)window.position.width, (int)window.position.height);
        }
    }
}
