#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityCli.Protocol;
using UnityEditor;
using UnityEngine;

namespace UnityCliBridge.Bridge.Editor
{
    public sealed class CliInstallerWindow : EditorWindow
    {
        private const string WindowTitle = "CLI Manager";
        private const string OpenWindowMenuItemPath = "Window/Unity CLI Manager";
        private const double AutoRefreshIntervalSeconds = 1d;
        private const double StateLoadFailureAutoRetryDelaySeconds = 10d;
        private static readonly Vector2 WindowMinSize = new Vector2(420f, 340f);
        private const string SkillTargetPrefKey = "UnityCliBridge.CliManager.SkillTarget";
        private const string SkillLocationPrefKeyPrefix = "UnityCliBridge.CliManager.SkillLocation.";
        private const string SkillCustomRootPrefKeyPrefix = "UnityCliBridge.CliManager.SkillCustomRoot.";

        private Vector2 _scrollPosition;
        private CliInstallStatus _status;
        private IReadOnlyList<InstalledCliVersion> _installedVersions = Array.Empty<InstalledCliVersion>();
        private IReadOnlyList<string> _orphanedInstalls = Array.Empty<string>();
        private InstalledCliVersion? _pendingVersionRemoval;
        private string? _pendingOrphanRemoval;
        private string _packageVersion = string.Empty;
        private string _protocolVersion = string.Empty;
        private string _latestReleaseVersion = string.Empty;
        private string _pathTargetVersion = string.Empty;
        private string _executablePath = string.Empty;
        private string _platformDisplayName = string.Empty;
        private string _downloadUrl = string.Empty;
        private string _releasePageUrl = string.Empty;
        private string _pathCommand = string.Empty;
        private string _errorMessage = string.Empty;
        private string _skillFeedbackMessage = string.Empty;
        private string _skillDestinationPath = string.Empty;
        private string _installedSkillVersion = string.Empty;
        private string _globalSkillPath = string.Empty;
        private string _cachedUpdateAvailabilityPackageVersion = string.Empty;
        private string _cachedUpdateAvailabilityLatestReleaseVersion = string.Empty;
        private MessageType _skillFeedbackType = MessageType.Info;
        private float _downloadProgress;
        private bool _hasLoadedState;
        private bool _isDownloading;
        private bool _isFetchingLatestVersion;
        private bool _latestReleaseCheckFailed;
        private bool _isUpdateAvailable;
        private bool _stateLoadFailed;
        private bool _isSkillInstalled;
        private bool _isGlobalSkillInstalled;
        private bool _skillRootResolved;
        private SkillTarget _skillTarget;
        private SkillInstallLocation _skillLocation;
        private string _skillCustomRoot = string.Empty;
        private string _unityProjectRoot = string.Empty;
        private string _userHomeRoot = string.Empty;
        private string _detectedGitRoot = string.Empty;
        private string _resolvedInstallRoot = string.Empty;
        private string _skillRootError = string.Empty;
        private string _gitRootMismatchWarning = string.Empty;
        private string? _pendingLegacyRemoval;
        private Texture2D? _headerIcon;
        private Texture2D? _tabIcon;
        private IReadOnlyList<string> _legacyCodexPaths = Array.Empty<string>();
        private IReadOnlyList<string> _duplicateWarnings = Array.Empty<string>();
        private double _nextAutoRefreshTime;
        private double _nextStateLoadRetryTime;

        [MenuItem(OpenWindowMenuItemPath)]
        private static void OpenWindow()
        {
            CliInstallerWindow window = GetWindow<CliInstallerWindow>();
            window.ApplyWindowChrome();
            window.Show();
        }

        private void OnEnable()
        {
            ApplyWindowChrome();
            LoadSkillPreferences();
            RefreshState(true);
        }

        private void OnFocus()
        {
            if (_tabIcon == null || _headerIcon == null)
            {
                ApplyWindowChrome();
            }

            if (!_isDownloading)
            {
                RefreshState(false);
            }
        }

        private void OnGUI()
        {
            if (!_hasLoadedState && !_isDownloading)
            {
                RefreshState(false);
            }

            CliManagerStyles.EnsureStyles();
            CliManagerStyles.SetWindowWidth(position.width);
            float contentWidth = CliManagerStyles.ScrollContentWidth();
            EditorGUIUtility.labelWidth = Mathf.Clamp(contentWidth * 0.34f, 96f, 140f);
            CliManagerStyles.DrawHeader(_headerIcon, FormatVersion(_packageVersion));

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition, false, false);
            CliManagerStyles.BeginScrollContent();

            GUILayout.Space(6f);
            DrawPackageInfoSection();
            GUILayout.Space(8f);
            DrawCliStatusSection();
            GUILayout.Space(8f);
            DrawActionSection();
            GUILayout.Space(8f);
            DrawInstalledVersionsSection();
            GUILayout.Space(8f);
            DrawPathSetupSection();
            GUILayout.Space(8f);
            DrawSkillSection();

            CliManagerStyles.EndScrollContent();
            EditorGUILayout.EndScrollView();

            ProcessPendingRemovals();
        }

        /// <summary>
        /// Removals run after every control for this event has been emitted. Doing the work inline
        /// from the button would reassign the lists mid-loop and draw the remaining rows shifted, and
        /// a quarantine during removal can change the control count too. ExitGUI aborts the rest of
        /// the event so the next pass lays out from the refreshed state.
        /// </summary>
        private void ProcessPendingRemovals()
        {
            InstalledCliVersion? versionToRemove = _pendingVersionRemoval;
            string? orphanToRemove = _pendingOrphanRemoval;
            string? legacyToRemove = _pendingLegacyRemoval;
            if (versionToRemove == null
                && string.IsNullOrWhiteSpace(orphanToRemove)
                && string.IsNullOrWhiteSpace(legacyToRemove))
            {
                return;
            }

            _pendingVersionRemoval = null;
            _pendingOrphanRemoval = null;
            _pendingLegacyRemoval = null;

            if (versionToRemove != null)
            {
                RemoveVersion(versionToRemove);
            }
            else if (!string.IsNullOrWhiteSpace(orphanToRemove))
            {
                RemoveOrphanedInstall(orphanToRemove!);
            }
            else
            {
                RemoveLegacyCodex(legacyToRemove!);
            }

            GUIUtility.ExitGUI();
        }

        private void DrawPackageInfoSection()
        {
            using (new CliManagerStyles.SectionScope("Package Info"))
            {
                DrawKeyValue("Package Version", FormatVersion(_packageVersion));
                DrawKeyValue("Wire Protocol", string.IsNullOrWhiteSpace(_protocolVersion) ? "-" : _protocolVersion);
                DrawLatestReleaseRow();
                DrawLinkButton("Repository", CliInstallerState.GetRepositoryUrl(), "Open Repository");
                DrawLinkButton("Release", _releasePageUrl, "Open Release");
            }
        }

        private void DrawCliStatusSection()
        {
            using (new CliManagerStyles.SectionScope("CLI Status"))
            {
                CliManagerStyles.BadgeField("Status", GetStatusLabel(), GetStatusBadgeKind());
                DrawKeyValue("Platform", _platformDisplayName);
                DrawKeyValue("PATH Target", GetPathTargetLabel());
                DrawSelectableValue("Path", _executablePath);
            }
        }

        private void DrawActionSection()
        {
            using (new CliManagerStyles.SectionScope("Actions"))
            {

                if (!string.IsNullOrWhiteSpace(_errorMessage))
                {
                    CliManagerStyles.DrawWrappedHelp(_errorMessage, MessageType.Error);
                }

                string releaseAvailabilityMessage = GetReleaseAvailabilityMessage();
                if (!string.IsNullOrWhiteSpace(releaseAvailabilityMessage))
                {
                    CliManagerStyles.DrawWrappedHelp(releaseAvailabilityMessage, MessageType.Info);
                }

                if (_status == CliInstallStatus.UpdateRequired)
                {
                    CliManagerStyles.DrawWrappedHelp(
                        "The PATH target does not point at a managed CLI version. An older CLI Manager (package 0.4.0 or earlier) overwrites it with a single flat binary. Click Install CLI to restore version dispatch.",
                        MessageType.Warning);
                }

                if (_isDownloading)
                {
                    Rect progressRect = GUILayoutUtility.GetRect(
                        0f,
                        18f,
                        GUILayout.MinWidth(0f),
                        GUILayout.ExpandWidth(true));
                    EditorGUI.ProgressBar(progressRect, _downloadProgress, "Downloading CLI...");
                    GUILayout.Space(4f);
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_errorMessage))
                {
                    if (CliManagerStyles.PrimaryButton("Retry", GUILayout.Height(28f)))
                    {
                        BeginInstall();
                    }

                    return;
                }

                if (string.IsNullOrWhiteSpace(_packageVersion))
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        CliManagerStyles.WideButton("Package version unknown", GUILayout.Height(28f));
                    }

                    return;
                }

                if (_isFetchingLatestVersion && string.IsNullOrWhiteSpace(_latestReleaseVersion))
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        CliManagerStyles.WideButton("Checking release...", GUILayout.Height(28f));
                    }

                    return;
                }

                if (IsPackageReleaseUnavailable())
                {
                    using (new EditorGUI.DisabledScope(true))
                    {
                        CliManagerStyles.WideButton("CLI " + FormatVersion(_packageVersion) + " not published", GUILayout.Height(28f));
                    }

                    return;
                }

                switch (_status)
                {
                    case CliInstallStatus.NotInstalled:
                    case CliInstallStatus.UpdateRequired:
                        if (CliManagerStyles.PrimaryButton("Install CLI", GUILayout.Height(28f)))
                        {
                            BeginInstall();
                        }

                        return;
                    case CliInstallStatus.UpToDate:
                        using (new EditorGUI.DisabledScope(true))
                        {
                            CliManagerStyles.WideButton("Up to date", GUILayout.Height(28f));
                        }

                        return;
                    default:
                        throw new InvalidOperationException("Unsupported CLI install status: " + _status);
                }
            }
        }

        private void DrawInstalledVersionsSection()
        {
            using (new CliManagerStyles.SectionScope("Installed Versions"))
            {
                CliManagerStyles.DrawWrappedHelp(
                    "Each Unity package version needs the CLI of the same version. The PATH target runs the newest one and hands off to an older one automatically when a project's bridge speaks an older protocol. Nothing is removed automatically.",
                    MessageType.None);

                if (_installedVersions.Count == 0)
                {
                    GUILayout.Label("No versioned CLI installs found.", GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
                }

                for (int i = 0; i < _installedVersions.Count; i++)
                {
                    InstalledCliVersion installedVersion = _installedVersions[i];
                    using (EditorGUILayout.HorizontalScope row = new EditorGUILayout.HorizontalScope())
                    {
                        bool isPathTarget = !string.IsNullOrWhiteSpace(_pathTargetVersion)
                            && string.Equals(installedVersion.Version, _pathTargetVersion, StringComparison.Ordinal);
                        if (isPathTarget && Event.current.type == EventType.Repaint)
                        {
                            CliManagerStyles.HighlightRow(row.rect);
                        }

                        string label = FormatVersion(installedVersion.Version)
                            + "  protocol " + installedVersion.ProtocolVersion
                            + (isPathTarget ? "  [PATH target]" : string.Empty);
                        GUILayout.Label(
                            label,
                            ClippedLineStyle(),
                            GUILayout.ExpandWidth(true),
                            GUILayout.MinWidth(0f));

                        if (GUILayout.Button("Remove", GUILayout.Width(72f)))
                        {
                            _pendingVersionRemoval = installedVersion;
                        }
                    }
                }

                DrawOrphanedInstalls();
            }
        }

        private void DrawOrphanedInstalls()
        {
            if (_orphanedInstalls.Count == 0)
            {
                return;
            }

            GUILayout.Space(8f);
            GUILayout.Label("Unidentified Binaries", EditorStyles.boldLabel, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
            CliManagerStyles.DrawWrappedHelp(
                "A CLI binary was found on the PATH target with no version recorded for it — typically a manual download. Its wire protocol is unknown, so it cannot be used for hand-off, but it was kept rather than deleted in case it is the only CLI you have for an older project. Remove it once you are sure you do not need it.",
                MessageType.Warning);

            for (int i = 0; i < _orphanedInstalls.Count; i++)
            {
                string orphanedInstall = _orphanedInstalls[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawFlexibleSelectable(orphanedInstall);

                    if (GUILayout.Button("Reveal", GUILayout.Width(72f)))
                    {
                        EditorUtility.RevealInFinder(orphanedInstall);
                    }

                    if (GUILayout.Button("Remove", GUILayout.Width(72f)))
                    {
                        _pendingOrphanRemoval = orphanedInstall;
                    }
                }
            }
        }

        private void DrawPathSetupSection()
        {
            using (new CliManagerStyles.SectionScope("PATH Setup"))
            {

                CliManagerStyles.DrawWrappedHelp("Add the install directory to your PATH to use the CLI from the terminal. A short, fixed path also saves tokens when AI agents invoke the CLI repeatedly.", MessageType.None);
                DrawSelectableValue("Command", _pathCommand);

                EditorGUI.BeginDisabledGroup(string.IsNullOrWhiteSpace(_pathCommand));
                if (CliManagerStyles.WideButton("Copy PATH command"))
                {
                    EditorGUIUtility.systemCopyBuffer = _pathCommand;
                }

                EditorGUI.EndDisabledGroup();
            }
        }

        private void DrawSkillSection()
        {
            using (new CliManagerStyles.SectionScope("AI Agent Skill"))
            {
                EditorGUI.BeginChangeCheck();
                Rect targetRow = CliManagerStyles.GetShrinkRow(EditorGUIUtility.singleLineHeight);
                _skillTarget = (SkillTarget)EditorGUI.EnumPopup(targetRow, "Target", _skillTarget);
                Rect locationRow = CliManagerStyles.GetShrinkRow(EditorGUIUtility.singleLineHeight);
                _skillLocation = (SkillInstallLocation)EditorGUI.EnumPopup(locationRow, "Location", _skillLocation);
                if (EditorGUI.EndChangeCheck())
                {
                    SaveSkillPreferences();
                    ClearSkillFeedback();
                    RefreshSkillState();
                }

                if (_skillLocation == SkillInstallLocation.GitRepository)
                {
                    CliManagerStyles.DrawWrappedHelp(
                        "Choose the folder that should contain the tool directory (.claude, .agents, or .grok). The Path row shows the skill directory that will be created inside it.",
                        MessageType.None);
                    DrawSelectableValue(
                        "Git root",
                        string.IsNullOrWhiteSpace(_detectedGitRoot) ? "(not found)" : _detectedGitRoot);
                    if (!string.IsNullOrWhiteSpace(_skillCustomRoot))
                    {
                        DrawSelectableValue("Chosen folder", _skillCustomRoot);
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("Choose folder", GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true)))
                        {
                            ChooseSkillFolder();
                        }

                        if (!string.IsNullOrWhiteSpace(_skillCustomRoot))
                        {
                            string clearLabel = string.IsNullOrWhiteSpace(_detectedGitRoot)
                                ? "Clear chosen folder"
                                : "Use detected git root";
                            if (GUILayout.Button(clearLabel, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true)))
                            {
                                _skillCustomRoot = string.Empty;
                                SaveSkillPreferences();
                                ClearSkillFeedback();
                                RefreshSkillState();
                            }
                        }
                    }
                }

                DrawSelectableValue("Path", _skillDestinationPath);
                CliManagerStyles.BadgeField("Skill", GetSkillStatusLabel(), GetSkillBadgeKind());

                if (!string.IsNullOrWhiteSpace(_skillRootError))
                {
                    CliManagerStyles.DrawWrappedHelp(_skillRootError, MessageType.Warning);
                }

                using (new EditorGUI.DisabledScope(!_skillRootResolved))
                {
                    if (_isSkillInstalled)
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            if (GUILayout.Button(GetSkillButtonLabel(), GUILayout.Height(24f), GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true)))
                            {
                                InstallSkill();
                            }

                            if (GUILayout.Button("Remove Skill", GUILayout.Height(24f), GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true)))
                            {
                                RemoveSkill();
                            }
                        }
                    }
                    else if (GUILayout.Button(GetSkillButtonLabel(), GUILayout.Height(24f), GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true)))
                    {
                        InstallSkill();
                    }
                }

                if (_skillLocation != SkillInstallLocation.UserHome
                    && _isGlobalSkillInstalled
                    && !SkillInstallPaths.PathsEqual(_resolvedInstallRoot, _userHomeRoot))
                {
                    CliManagerStyles.DrawWrappedHelp(
                        "A user-home copy of this skill is also installed at `" + _globalSkillPath + "`. It may take precedence over this copy. Set Location to User (home) to update or remove that copy.",
                        MessageType.Warning);
                }

                if (!string.IsNullOrWhiteSpace(_gitRootMismatchWarning))
                {
                    CliManagerStyles.DrawWrappedHelp(_gitRootMismatchWarning, MessageType.Warning);
                }

                for (int i = 0; i < _duplicateWarnings.Count; i++)
                {
                    CliManagerStyles.DrawWrappedHelp(_duplicateWarnings[i], MessageType.Warning);
                }

                DrawLegacyCodexCopies();

                if (!string.IsNullOrWhiteSpace(_skillFeedbackMessage))
                {
                    CliManagerStyles.DrawWrappedHelp(_skillFeedbackMessage, _skillFeedbackType);
                }
            }
        }

        private void DrawLegacyCodexCopies()
        {
            if (_legacyCodexPaths.Count == 0)
            {
                return;
            }

            CliManagerStyles.DrawWrappedHelp(
                "A deprecated Codex skill is still installed under .codex/skills. Codex now reads .agents/skills, and the old copy can be loaded as well. Remove it once you no longer need it.",
                MessageType.Warning);

            for (int i = 0; i < _legacyCodexPaths.Count; i++)
            {
                string legacyPath = _legacyCodexPaths[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    DrawFlexibleSelectable(legacyPath);

                    if (GUILayout.Button("Remove", GUILayout.Width(72f)))
                    {
                        _pendingLegacyRemoval = legacyPath;
                    }
                }
            }
        }

        private void DrawLatestReleaseRow()
        {
            const float refreshWidth = 72f;
            const float gap = 4f;
            string label = GetLatestReleaseVersionLabel();
            bool update = IsUpdateAvailable();

            float predicted = CliManagerStyles.SectionInnerWidth();
            float predictedLabel = CliManagerStyles.LabelColumnWidth(predicted);
            float predictedValue = Mathf.Max(32f, predicted - predictedLabel - refreshWidth - (gap * 2f));
            float height = EditorGUIUtility.singleLineHeight;
            if (update)
            {
                CliManagerStyles.MeasureBadge(label, CliManagerStyles.BadgeKind.Update, predictedValue, out _, out float badgeHeight);
                height = Mathf.Max(height, badgeHeight);
            }

            Rect row = CliManagerStyles.GetShrinkRow(height);
            float labelWidth = CliManagerStyles.LabelColumnWidth(row.width);
            float buttonWidth = Mathf.Min(refreshWidth, Mathf.Max(0f, row.width - labelWidth - gap));
            Rect labelRect = new Rect(row.x, row.y, labelWidth, row.height);
            Rect buttonRect = new Rect(row.xMax - buttonWidth, row.y, buttonWidth, EditorGUIUtility.singleLineHeight);
            if (row.height > buttonRect.height)
            {
                buttonRect.y += (row.height - buttonRect.height) * 0.5f;
            }

            float valueX = labelRect.xMax + gap;
            float valueWidth = Mathf.Max(0f, buttonRect.x - gap - valueX);
            Rect valueRect = new Rect(valueX, row.y, valueWidth, row.height);

            EditorGUI.LabelField(labelRect, "Latest Release");
            if (update)
            {
                CliManagerStyles.DrawBadge(valueRect, label, CliManagerStyles.BadgeKind.Update);
            }
            else
            {
                EditorGUI.LabelField(valueRect, label, ClippedLineStyle());
            }

            using (new EditorGUI.DisabledScope(_isFetchingLatestVersion))
            {
                if (GUI.Button(buttonRect, "Refresh"))
                {
                    RefreshState(true);
                    RefreshLatestReleaseVersion(true);
                }
            }
        }

        private void DrawLinkButton(string label, string url, string buttonLabel)
        {
            Rect row = CliManagerStyles.GetShrinkRow(EditorGUIUtility.singleLineHeight);
            float labelWidth = CliManagerStyles.LabelColumnWidth(row.width);
            Rect labelRect = new Rect(row.x, row.y, labelWidth, row.height);
            Rect buttonRect = new Rect(labelRect.xMax, row.y, Mathf.Max(0f, row.xMax - labelRect.xMax), row.height);
            EditorGUI.LabelField(labelRect, label);

            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(url)))
            {
                if (GUI.Button(buttonRect, buttonLabel))
                {
                    Application.OpenURL(url);
                }
            }
        }

        private static void DrawKeyValue(string label, string value)
        {
            Rect row = CliManagerStyles.GetShrinkRow(EditorGUIUtility.singleLineHeight);
            float labelWidth = CliManagerStyles.LabelColumnWidth(row.width);
            EditorGUI.LabelField(new Rect(row.x, row.y, labelWidth, row.height), label);
            EditorGUI.LabelField(
                new Rect(row.x + labelWidth, row.y, Mathf.Max(0f, row.width - labelWidth), row.height),
                value ?? string.Empty,
                ClippedLineStyle());
        }

        private void DrawSelectableValue(string label, string value)
        {
            GUILayout.Label(label ?? string.Empty, GUILayout.MinWidth(0f), GUILayout.ExpandWidth(true));
            DrawBoundedSelectable(value ?? string.Empty);
        }

        /// <summary>
        /// A path or command field. The layout width is capped to the section, and the text
        /// clips or scrolls inside the field, so a long path cannot widen the window.
        /// </summary>
        private static void DrawBoundedSelectable(string value)
        {
            GUIStyle style = CliManagerStyles.ClippedFieldStyle();
            float width = CliManagerStyles.SectionContentWidth();
            Rect rect = GUILayoutUtility.GetRect(
                0f,
                EditorGUIUtility.singleLineHeight,
                style,
                GUILayout.ExpandWidth(true),
                GUILayout.MinWidth(0f),
                GUILayout.MaxWidth(width),
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUI.SelectableLabel(rect, value, style);
        }

        private static GUIStyle ClippedLineStyle()
        {
            return new GUIStyle(EditorStyles.label)
            {
                wordWrap = false,
                clipping = TextClipping.Clip,
                alignment = TextAnchor.MiddleLeft,
            };
        }

        private static void DrawFlexibleSelectable(string value)
        {
            GUIStyle style = CliManagerStyles.ClippedFieldStyle();
            Rect rect = GUILayoutUtility.GetRect(
                0f,
                EditorGUIUtility.singleLineHeight,
                style,
                GUILayout.ExpandWidth(true),
                GUILayout.MinWidth(0f),
                GUILayout.Height(EditorGUIUtility.singleLineHeight));
            EditorGUI.SelectableLabel(rect, value ?? string.Empty, style);
        }

        private void InstallSkill()
        {
            if (!_skillRootResolved)
            {
                _skillFeedbackType = MessageType.Error;
                _skillFeedbackMessage = string.IsNullOrWhiteSpace(_skillRootError)
                    ? "Choose an install location before installing the skill."
                    : _skillRootError;
                return;
            }

            try
            {
                if (_isSkillInstalled)
                {
                    bool shouldOverwrite = EditorUtility.DisplayDialog(
                        "Overwrite Skill?",
                        "A skill is already installed at:\n" + _skillDestinationPath + "\nOverwrite it?",
                        "Overwrite",
                        "Cancel");
                    if (!shouldOverwrite)
                    {
                        return;
                    }
                }

                SkillInstaller.Install(_skillTarget, _resolvedInstallRoot);
                RefreshSkillState();
                _skillFeedbackType = MessageType.Info;
                _skillFeedbackMessage = "Installed to: " + _skillDestinationPath;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                _skillFeedbackType = MessageType.Error;
                _skillFeedbackMessage = exception.Message;
            }
        }

        private void RemoveSkill()
        {
            if (!_skillRootResolved)
            {
                _skillFeedbackType = MessageType.Error;
                _skillFeedbackMessage = string.IsNullOrWhiteSpace(_skillRootError)
                    ? "Choose an install location before removing the skill."
                    : _skillRootError;
                return;
            }

            string removedPath = _skillDestinationPath;
            bool shouldRemove = EditorUtility.DisplayDialog(
                "Remove Skill?",
                "This will remove the installed skill at:\n" + removedPath,
                "Remove",
                "Cancel");
            if (!shouldRemove)
            {
                return;
            }

            try
            {
                bool removed = SkillInstaller.Remove(_skillTarget, _resolvedInstallRoot);
                RefreshSkillState();

                if (removed)
                {
                    _skillFeedbackType = MessageType.Info;
                    _skillFeedbackMessage = "Removed from: " + removedPath;
                }
                else
                {
                    _skillFeedbackType = MessageType.Warning;
                    _skillFeedbackMessage = "Nothing to remove at: " + removedPath;
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                _skillFeedbackType = MessageType.Error;
                _skillFeedbackMessage = exception.Message;
            }
        }

        private void BeginInstall()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_packageVersion) || string.IsNullOrWhiteSpace(_downloadUrl))
                {
                    _errorMessage = string.Empty;
                    Repaint();
                    return;
                }

                _errorMessage = string.Empty;

                // The binary for this package version is already on disk (a stale or clobbered PATH
                // target is the usual reason we get here). Repointing does not need a 78MB download.
                if (CliInstallerState.IsVersionInstalled(_packageVersion))
                {
                    CliInstallerState.FinalizeInstall(_packageVersion);
                    RefreshState(true);
                    Repaint();
                    return;
                }

                _downloadProgress = 0f;
                _isDownloading = true;
                Repaint();

                CliDownloader.DownloadAndInstallAsync(
                    _downloadUrl,
                    _packageVersion,
                    CliInstallerState.GetVersionInstallDirectory(_packageVersion),
                    HandleDownloadProgress,
                    HandleDownloadError,
                    HandleDownloadComplete);
            }
            catch (Exception exception)
            {
                _isDownloading = false;
                _errorMessage = exception.Message;
                Repaint();
            }
        }

        private void RemoveVersion(InstalledCliVersion installedVersion)
        {
            bool shouldRemove = EditorUtility.DisplayDialog(
                "Remove CLI " + FormatVersion(installedVersion.Version) + "?",
                "This will delete:\n" + installedVersion.Directory
                    + "\n\nProjects on a package version that speaks protocol "
                    + installedVersion.ProtocolVersion
                    + " will stop working until it is installed again.",
                "Remove",
                "Cancel");
            if (!shouldRemove)
            {
                return;
            }

            try
            {
                _errorMessage = string.Empty;
                CliInstallerState.RemoveVersion(installedVersion.Version);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                _errorMessage = exception.Message;
            }

            RefreshState(false);
            Repaint();
        }

        private void RemoveOrphanedInstall(string orphanedInstall)
        {
            bool shouldRemove = EditorUtility.DisplayDialog(
                "Remove unidentified CLI binary?",
                "This will delete:\n" + orphanedInstall
                    + "\n\nIts wire protocol is unknown. If a project on an older package version still needs it, you will have to reinstall that CLI yourself.",
                "Remove",
                "Cancel");
            if (!shouldRemove)
            {
                return;
            }

            try
            {
                _errorMessage = string.Empty;
                CliInstallerState.RemoveOrphanedInstall(orphanedInstall);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                _errorMessage = exception.Message;
            }

            RefreshState(false);
            Repaint();
        }

        private void HandleDownloadProgress(float progress)
        {
            _downloadProgress = progress;
            Repaint();
        }

        private void HandleDownloadError(string errorMessage)
        {
            _isDownloading = false;
            _errorMessage = errorMessage;
            RefreshState(false);
            Repaint();
        }

        private void HandleDownloadComplete()
        {
            _isDownloading = false;
            _downloadProgress = 1f;
            RefreshState(true);
            Repaint();
        }

        private void OnInspectorUpdate()
        {
            if (_isDownloading)
            {
                return;
            }

            double currentTime = EditorApplication.timeSinceStartup;
            if (currentTime < _nextAutoRefreshTime)
            {
                return;
            }

            _nextAutoRefreshTime = currentTime + AutoRefreshIntervalSeconds;

            bool shouldRepaint = false;
            if (_hasLoadedState
                && currentTime >= _nextStateLoadRetryTime
                && (_stateLoadFailed || HasInstallStateChanged()))
            {
                RefreshState(false);
                shouldRepaint = true;
            }

            if (CliInstallerState.IsLatestReleaseCacheExpired())
            {
                string previousLatestReleaseVersion = _latestReleaseVersion;
                bool wasFetchingLatestVersion = _isFetchingLatestVersion;
                bool previousReleaseCheckFailed = _latestReleaseCheckFailed;

                RefreshLatestReleaseVersion();

                shouldRepaint = shouldRepaint
                    || !string.Equals(previousLatestReleaseVersion, _latestReleaseVersion, StringComparison.Ordinal)
                    || wasFetchingLatestVersion != _isFetchingLatestVersion
                    || previousReleaseCheckFailed != _latestReleaseCheckFailed;
            }

            if (shouldRepaint)
            {
                Repaint();
            }
        }

        private void RefreshState(bool shouldClearError)
        {
            if (shouldClearError)
            {
                _errorMessage = string.Empty;
            }

            try
            {
                _packageVersion = CliInstallerState.GetPackageVersion();
                _protocolVersion = CliInstallerState.GetProtocolVersion();
                _installedVersions = CliInstallerState.ListInstalledVersions();
                _orphanedInstalls = CliInstallerState.ListOrphanedInstalls();
                _pathTargetVersion = CliInstallerState.GetPathTargetVersion() ?? string.Empty;
                _executablePath = CliInstallerState.GetExecutablePath();
                _platformDisplayName = CliInstallerState.GetPlatformDisplayName();
                _pathCommand = GetPathCommand();
                RefreshLatestReleaseVersion();
                RefreshSkillState();
                _hasLoadedState = true;
                _stateLoadFailed = false;
                _nextStateLoadRetryTime = 0d;
            }
            catch (Exception exception)
            {
                _hasLoadedState = true;
                _stateLoadFailed = true;
                _nextStateLoadRetryTime = EditorApplication.timeSinceStartup + StateLoadFailureAutoRetryDelaySeconds;
                _packageVersion = string.Empty;
                _protocolVersion = string.Empty;
                _latestReleaseVersion = string.Empty;
                _installedVersions = Array.Empty<InstalledCliVersion>();
                _orphanedInstalls = Array.Empty<string>();
                _pathTargetVersion = string.Empty;
                _executablePath = string.Empty;
                _platformDisplayName = string.Empty;
                _downloadUrl = string.Empty;
                _releasePageUrl = string.Empty;
                _pathCommand = string.Empty;
                _status = CliInstallStatus.NotInstalled;
                _isFetchingLatestVersion = false;
                _latestReleaseCheckFailed = false;
                ResetUpdateAvailabilityCache();
                RefreshSkillState();

                if (string.IsNullOrWhiteSpace(_errorMessage))
                {
                    _errorMessage = exception.Message;
                }
            }
        }

        private void RefreshLatestReleaseVersion(bool forceRefresh = false)
        {
            _latestReleaseVersion = CliInstallerState.GetCachedLatestReleaseVersion() ?? string.Empty;
            RefreshReleaseDerivedState();
            if (_isFetchingLatestVersion || (!forceRefresh && !CliInstallerState.IsLatestReleaseCacheExpired()))
            {
                return;
            }

            _isFetchingLatestVersion = true;
            _latestReleaseCheckFailed = false;
            CliInstallerState.FetchLatestReleaseVersion(HandleLatestReleaseVersionFetched);
        }

        private void HandleLatestReleaseVersionFetched(CliInstallerState.LatestReleaseFetchResult result)
        {
            if (this == null)
            {
                return;
            }

            try
            {
                _latestReleaseVersion = result.LatestReleaseVersion ?? string.Empty;
                _latestReleaseCheckFailed = !result.Succeeded;
                _isFetchingLatestVersion = false;
                RefreshReleaseDerivedState();
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                _errorMessage = exception.Message;
            }
            finally
            {
                if (this != null)
                {
                    Repaint();
                }
            }
        }

        private void RefreshReleaseDerivedState()
        {
            // The CLI must match this package's version, not the newest release: their protocol
            // versions are what have to line up, and only the same-version CLI is guaranteed to.
            _downloadUrl = string.IsNullOrWhiteSpace(_packageVersion)
                ? string.Empty
                : CliInstallerState.GetDownloadUrl(_packageVersion);
            _releasePageUrl = string.IsNullOrWhiteSpace(_latestReleaseVersion)
                ? string.Empty
                : CliInstallerState.GetReleasePageUrl(_latestReleaseVersion);

            _status = CliInstallerState.GetStatus(_packageVersion);
            UpdateUpdateAvailabilityCache();
        }

        private string GetStatusLabel()
        {
            switch (_status)
            {
                case CliInstallStatus.NotInstalled:
                    return string.IsNullOrWhiteSpace(_packageVersion)
                        ? "Not Installed"
                        : "Not Installed (needs CLI " + FormatVersion(_packageVersion) + ")";
                case CliInstallStatus.UpToDate:
                    return "Installed (" + FormatVersion(_packageVersion) + ")";
                case CliInstallStatus.UpdateRequired:
                    return "Installed (" + FormatVersion(_packageVersion) + ") - PATH target needs repair";
                default:
                    throw new InvalidOperationException("Unsupported CLI install status: " + _status);
            }
        }

        private string GetPathTargetLabel()
        {
            if (!string.IsNullOrWhiteSpace(_pathTargetVersion))
            {
                return FormatVersion(_pathTargetVersion);
            }

            return CliInstallerState.IsUnmanagedPathTargetBinaryPresent()
                ? "Unmanaged binary (no version info)"
                : "Not set";
        }

        /// <summary>
        /// True only when the release check succeeded and told us no CLI matching this package
        /// version exists yet. A failed or in-flight check must never block a valid install.
        /// </summary>
        private bool IsPackageReleaseUnavailable()
        {
            if (_isFetchingLatestVersion || _latestReleaseCheckFailed || string.IsNullOrWhiteSpace(_packageVersion))
            {
                return false;
            }

            return string.IsNullOrWhiteSpace(_latestReleaseVersion)
                || CliInstallerState.CompareVersions(_latestReleaseVersion, _packageVersion) < 0;
        }

        private string GetLatestReleaseVersionLabel()
        {
            if (_isFetchingLatestVersion && string.IsNullOrWhiteSpace(_latestReleaseVersion))
            {
                return "Checking...";
            }

            if (_latestReleaseCheckFailed && string.IsNullOrWhiteSpace(_latestReleaseVersion))
            {
                return "Check failed";
            }

            return FormatVersion(_latestReleaseVersion);
        }

        private bool IsUpdateAvailable()
        {
            return _isUpdateAvailable;
        }

        private string GetReleaseAvailabilityMessage()
        {
            if (_isFetchingLatestVersion && string.IsNullOrWhiteSpace(_latestReleaseVersion))
            {
                return "Checking the latest published CLI release...";
            }

            if (_latestReleaseCheckFailed)
            {
                return string.IsNullOrWhiteSpace(_latestReleaseVersion)
                    ? "Could not check the latest published CLI release. You can retry the release check."
                    : "Could not refresh the latest published CLI release. Using the cached release version.";
            }

            if (string.IsNullOrWhiteSpace(_latestReleaseVersion))
            {
                return "No published CLI release is currently available. Only draft releases may exist right now.";
            }

            if (!string.IsNullOrWhiteSpace(_packageVersion)
                && CliInstallerState.CompareVersions(_latestReleaseVersion, _packageVersion) < 0)
            {
                return "CLI " + FormatVersion(_packageVersion) + " (matching this package) has not been published yet. The latest published release is "
                    + FormatVersion(_latestReleaseVersion) + ". Draft releases are ignored by the installer.";
            }

            return string.Empty;
        }

        private void ResetUpdateAvailabilityCache()
        {
            _cachedUpdateAvailabilityPackageVersion = string.Empty;
            _cachedUpdateAvailabilityLatestReleaseVersion = string.Empty;
            _isUpdateAvailable = false;
        }

        private void UpdateUpdateAvailabilityCache()
        {
            if (string.Equals(_cachedUpdateAvailabilityPackageVersion, _packageVersion, StringComparison.Ordinal)
                && string.Equals(_cachedUpdateAvailabilityLatestReleaseVersion, _latestReleaseVersion, StringComparison.Ordinal))
            {
                return;
            }

            _cachedUpdateAvailabilityPackageVersion = _packageVersion;
            _cachedUpdateAvailabilityLatestReleaseVersion = _latestReleaseVersion;

            if (string.IsNullOrWhiteSpace(_latestReleaseVersion) || string.IsNullOrWhiteSpace(_packageVersion))
            {
                _isUpdateAvailable = false;
                return;
            }

            _isUpdateAvailable = CliInstallerState.CompareVersions(_packageVersion, _latestReleaseVersion) < 0;
        }

        private void RefreshSkillState()
        {
            _unityProjectRoot = string.Empty;
            _userHomeRoot = string.Empty;
            _detectedGitRoot = string.Empty;
            _resolvedInstallRoot = string.Empty;
            _skillRootError = string.Empty;
            _skillRootResolved = false;
            _skillDestinationPath = string.Empty;
            _gitRootMismatchWarning = string.Empty;
            _duplicateWarnings = Array.Empty<string>();
            _legacyCodexPaths = Array.Empty<string>();
            _isSkillInstalled = false;
            _installedSkillVersion = string.Empty;
            _globalSkillPath = string.Empty;
            _isGlobalSkillInstalled = false;

            try
            {
                _unityProjectRoot = SkillInstaller.GetUnityProjectRoot();
            }
            catch (Exception exception)
            {
                _skillRootError = exception.Message;
            }

            try
            {
                _userHomeRoot = SkillInstaller.GetUserHomeRoot();
            }
            catch (Exception exception)
            {
                if (string.IsNullOrWhiteSpace(_skillRootError))
                {
                    _skillRootError = exception.Message;
                }
            }

            if (!string.IsNullOrWhiteSpace(_unityProjectRoot))
            {
                _detectedGitRoot = SkillInstallPaths.TryFindGitRoot(_unityProjectRoot) ?? string.Empty;
            }

            if (SkillInstallPaths.TryResolveInstallRoot(
                    _skillLocation,
                    _unityProjectRoot,
                    string.IsNullOrWhiteSpace(_detectedGitRoot) ? null : _detectedGitRoot,
                    _skillCustomRoot,
                    _userHomeRoot,
                    out string installRoot,
                    out string? rootError))
            {
                _resolvedInstallRoot = installRoot;
                _skillRootResolved = true;
                _skillRootError = string.Empty;
            }
            else if (!string.IsNullOrWhiteSpace(rootError) && string.IsNullOrWhiteSpace(_skillRootError))
            {
                _skillRootError = rootError ?? string.Empty;
            }

            try
            {
                if (_skillRootResolved)
                {
                    _skillDestinationPath = SkillInstaller.GetDestination(_skillTarget, _resolvedInstallRoot);
                    _isSkillInstalled = SkillInstaller.IsSkillInstalled(_skillTarget, _resolvedInstallRoot);
                    _installedSkillVersion = SkillInstaller.GetInstalledSkillVersion(_skillTarget, _resolvedInstallRoot) ?? string.Empty;
                    _duplicateWarnings = SkillInstallPaths.DescribeDuplicateVisibility(
                        _skillTarget,
                        SkillInstaller.IsSkillInstalled(SkillTarget.ClaudeCode, _resolvedInstallRoot),
                        SkillInstaller.IsSkillInstalled(SkillTarget.Codex, _resolvedInstallRoot),
                        SkillInstaller.IsSkillInstalled(SkillTarget.GrokBuild, _resolvedInstallRoot),
                        SkillInstallPaths.PathsEqual(_resolvedInstallRoot, _userHomeRoot));
                }
            }
            catch (Exception exception)
            {
                _skillDestinationPath = exception.Message;
                _skillRootResolved = false;
                _skillRootError = exception.Message;
            }

            try
            {
                _globalSkillPath = SkillInstaller.GetDestination(_skillTarget, SkillScope.Global);
                _isGlobalSkillInstalled = SkillInstaller.IsSkillInstalled(_skillTarget, SkillScope.Global);
            }
            catch (Exception exception)
            {
                _globalSkillPath = exception.Message;
                _isGlobalSkillInstalled = false;
            }

            _gitRootMismatchWarning = SkillInstallPaths.GitRootMismatchWarning(
                _skillLocation,
                _unityProjectRoot,
                string.IsNullOrWhiteSpace(_detectedGitRoot) ? null : _detectedGitRoot) ?? string.Empty;

            try
            {
                List<string> roots = new List<string>();
                if (!string.IsNullOrWhiteSpace(_unityProjectRoot))
                {
                    roots.Add(_unityProjectRoot);
                }

                if (!string.IsNullOrWhiteSpace(_detectedGitRoot))
                {
                    roots.Add(_detectedGitRoot);
                }

                if (!string.IsNullOrWhiteSpace(_userHomeRoot))
                {
                    roots.Add(_userHomeRoot);
                }

                if (!string.IsNullOrWhiteSpace(_skillCustomRoot))
                {
                    roots.Add(_skillCustomRoot);
                }

                _legacyCodexPaths = SkillInstaller.FindLegacyCodexInstalls(roots);
            }
            catch (Exception)
            {
                _legacyCodexPaths = Array.Empty<string>();
            }
        }

        private CliManagerStyles.BadgeKind GetStatusBadgeKind()
        {
            switch (_status)
            {
                case CliInstallStatus.NotInstalled:
                    return CliManagerStyles.BadgeKind.None;
                case CliInstallStatus.UpToDate:
                    return CliManagerStyles.BadgeKind.Installed;
                case CliInstallStatus.UpdateRequired:
                    return CliManagerStyles.BadgeKind.Update;
                default:
                    return CliManagerStyles.BadgeKind.None;
            }
        }

        private CliManagerStyles.BadgeKind GetSkillBadgeKind()
        {
            if (!_isSkillInstalled)
            {
                return CliManagerStyles.BadgeKind.None;
            }

            if (!string.IsNullOrWhiteSpace(_installedSkillVersion)
                && !string.IsNullOrWhiteSpace(_packageVersion)
                && CliInstallerState.CompareVersions(_installedSkillVersion, _packageVersion) < 0)
            {
                return CliManagerStyles.BadgeKind.Update;
            }

            return CliManagerStyles.BadgeKind.Installed;
        }

        private string GetSkillStatusLabel()
        {
            if (!_isSkillInstalled)
            {
                return "Not installed";
            }

            if (string.IsNullOrWhiteSpace(_installedSkillVersion))
            {
                return "Installed (version unknown)";
            }

            string installedLabel = "Installed (" + FormatVersion(_installedSkillVersion) + ")";
            if (!string.IsNullOrWhiteSpace(_packageVersion)
                && CliInstallerState.CompareVersions(_installedSkillVersion, _packageVersion) < 0)
            {
                return installedLabel + " -> update available (" + FormatVersion(_packageVersion) + ")";
            }

            return installedLabel;
        }

        private string GetSkillButtonLabel()
        {
            if (!_isSkillInstalled)
            {
                return "Install Skill";
            }

            if (string.IsNullOrWhiteSpace(_installedSkillVersion))
            {
                return "Update Skill";
            }

            if (!string.IsNullOrWhiteSpace(_packageVersion)
                && CliInstallerState.CompareVersions(_installedSkillVersion, _packageVersion) < 0)
            {
                return "Update Skill";
            }

            return "Reinstall Skill";
        }

        private bool HasInstallStateChanged()
        {
            if (string.IsNullOrWhiteSpace(_packageVersion))
            {
                return false;
            }

            bool isVersionInstalled = CliInstallerState.IsVersionInstalled(_packageVersion);
            bool wasVersionInstalled = _status != CliInstallStatus.NotInstalled;
            if (isVersionInstalled != wasVersionInstalled)
            {
                return true;
            }

            string pathTargetVersion = CliInstallerState.GetPathTargetVersion() ?? string.Empty;
            return !string.Equals(pathTargetVersion, _pathTargetVersion, StringComparison.Ordinal);
        }

        private static string GetPathCommand()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor:
                    return "export PATH=\"$HOME/.unity-cli-bridge/unity-cli:$PATH\"";
                case RuntimePlatform.WindowsEditor:
                    return "$env:Path = \"$env:USERPROFILE\\.unity-cli-bridge\\unity-cli;$env:Path\"";
                default:
                    throw new PlatformNotSupportedException("CLI Installer only supports macOS arm64 and Windows x64 editors.");
            }
        }

        private static string FormatVersion(string version)
        {
            return string.IsNullOrWhiteSpace(version)
                ? "-"
                : "v" + version.Trim().TrimStart('v', 'V');
        }

        private void ApplyWindowChrome()
        {
            EnsureIcons();
            titleContent = _tabIcon != null
                ? new GUIContent(WindowTitle, _tabIcon!)
                : new GUIContent(WindowTitle);
            minSize = WindowMinSize;
        }

        private void EnsureIcons()
        {
            string? directory = GetScriptDirectory();
            if (directory == null)
            {
                return;
            }

            _headerIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(directory + "/Icons/unity-cli-bridge-icon.png");
            _tabIcon = AssetDatabase.LoadAssetAtPath<Texture2D>(directory + "/Icons/unity-cli-bridge-tab.png");
        }

        private string? GetScriptDirectory()
        {
            MonoScript script = MonoScript.FromScriptableObject(this);
            if (script == null)
            {
                return null;
            }

            string assetPath = AssetDatabase.GetAssetPath(script!);
            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }

            string? directory = Path.GetDirectoryName(assetPath);
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            return directory.Replace('\\', '/');
        }

        private void ChooseSkillFolder()
        {
            string start = _skillCustomRoot;
            if (string.IsNullOrWhiteSpace(start))
            {
                start = _detectedGitRoot;
            }

            if (string.IsNullOrWhiteSpace(start))
            {
                start = _unityProjectRoot;
            }

            string picked = EditorUtility.OpenFolderPanel(
                "Choose skill install folder",
                start ?? string.Empty,
                string.Empty);
            if (string.IsNullOrEmpty(picked))
            {
                return;
            }

            _skillCustomRoot = picked;
            _skillLocation = SkillInstallLocation.GitRepository;
            SaveSkillPreferences();
            ClearSkillFeedback();
            RefreshSkillState();
        }

        private void RemoveLegacyCodex(string legacyPath)
        {
            bool shouldRemove = EditorUtility.DisplayDialog(
                "Remove legacy Codex skill?",
                "This will delete the deprecated copy at:\n" + legacyPath
                    + "\n\nCodex now reads .agents/skills. This only removes the old .codex copy.",
                "Remove",
                "Cancel");
            if (!shouldRemove)
            {
                return;
            }

            try
            {
                bool removed = SkillInstaller.RemoveLegacyCodex(legacyPath);
                RefreshSkillState();
                if (removed)
                {
                    _skillFeedbackType = MessageType.Info;
                    _skillFeedbackMessage = "Removed legacy copy from: " + legacyPath;
                }
                else
                {
                    _skillFeedbackType = MessageType.Warning;
                    _skillFeedbackMessage = "Nothing to remove at: " + legacyPath;
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                _skillFeedbackType = MessageType.Error;
                _skillFeedbackMessage = exception.Message;
            }
        }

        private void ClearSkillFeedback()
        {
            _skillFeedbackType = MessageType.None;
            _skillFeedbackMessage = string.Empty;
        }

        private void LoadSkillPreferences()
        {
            _skillTarget = ReadEnumPref(SkillTargetPrefKey, SkillTarget.ClaudeCode);
            string suffix = GetProjectPrefSuffix();
            _skillLocation = ReadEnumPref(SkillLocationPrefKeyPrefix + suffix, SkillInstallLocation.UnityProject);
            _skillCustomRoot = EditorPrefs.GetString(SkillCustomRootPrefKeyPrefix + suffix, string.Empty);
        }

        private void SaveSkillPreferences()
        {
            EditorPrefs.SetInt(SkillTargetPrefKey, (int)_skillTarget);
            string suffix = GetProjectPrefSuffix();
            EditorPrefs.SetInt(SkillLocationPrefKeyPrefix + suffix, (int)_skillLocation);
            EditorPrefs.SetString(SkillCustomRootPrefKeyPrefix + suffix, _skillCustomRoot ?? string.Empty);
        }

        private static string GetProjectPrefSuffix()
        {
            try
            {
                return SkillInstaller.GetUnityProjectRoot();
            }
            catch (Exception)
            {
                return "unknown";
            }
        }

        private static TEnum ReadEnumPref<TEnum>(string key, TEnum fallback)
            where TEnum : struct
        {
            int stored = EditorPrefs.GetInt(key, Convert.ToInt32(fallback));
            if (Enum.IsDefined(typeof(TEnum), stored))
            {
                return (TEnum)Enum.ToObject(typeof(TEnum), stored);
            }

            return fallback;
        }
    }
}
