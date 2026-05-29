using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.SceneManagement;

public class AnimationToPngSequenceBakerWindow : EditorWindow
{
    private const string DefaultOutputFolder = "Assets/BakedAnimationFrames";
    private const int FallbackBakeLayer = 31;
    private const string AtlasSuffix = "_atlas";
    private const string BakedAnimationSuffix = "_baked";
    private const string EditorPrefsPrefix = "AnimationToPngSequenceBaker.";

    [SerializeField] private DefaultAsset outputFolder;
    [SerializeField] private int framesPerClip = 12;
    [SerializeField] private int outputWidth = 256;
    [SerializeField] private int outputHeight = 256;
    [SerializeField] private float boundsPaddingPercent = 0.08f;
    [SerializeField] private bool includeInactiveChildren = true;
    [SerializeField] private bool overwriteExistingFiles = true;
    [SerializeField] private bool showOnlyLikelyBakeablePrefabs = true;

    [SerializeField] private Vector2 scrollPosition;
    [SerializeField] private Vector2 prefabScrollPosition;
    [SerializeField] private Vector2 clipScrollPosition;
    [SerializeField] private string prefabSearch = string.Empty;
    [SerializeField] private string clipSearch = string.Empty;
    [SerializeField] private string lastBakedFolderAssetPath = string.Empty;
    [SerializeField] private Vector2 resultScrollPosition;

    private readonly HashSet<string> selectedPrefabGuids = new();
    private readonly HashSet<string> selectedClipGuids = new();
    private readonly Dictionary<string, bool> foldoutStates = new();
    private readonly List<BakeResult> bakeResults = new();

    private List<AssetSelectionItem<GameObject>> prefabItems = new();
    private List<AssetSelectionItem<AnimationClip>> clipItems = new();
    private FolderNode<GameObject> prefabRoot;
    private FolderNode<AnimationClip> clipRoot;

    [MenuItem("Tools/Animation/Animation To PNG Sequence Baker")]
    private static void OpenWindow()
    {
        GetWindow<AnimationToPngSequenceBakerWindow>("Anim PNG Baker");
    }

    private void OnEnable()
    {
        LoadSettings();
        RefreshAssetCaches();
    }

    private void OnGUI()
    {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        DrawHeader();
        DrawSettings();

        EditorGUILayout.Space(8f);
        DrawSelectionArea(
            title: "Prefabs",
            helpText: "Only prefabs with a usable Animator and visible Renderer are listed.",
            items: prefabItems,
            root: prefabRoot,
            selectedGuids: selectedPrefabGuids,
            searchText: ref prefabSearch,
            scroll: ref prefabScrollPosition,
            sectionKey: "prefab");

        EditorGUILayout.Space(8f);
        DrawSelectionArea(
            title: "Animation Clips",
            helpText: "Only AnimationClip assets are listed. Folder checkboxes select all clips under that folder.",
            items: clipItems,
            root: clipRoot,
            selectedGuids: selectedClipGuids,
            searchText: ref clipSearch,
            scroll: ref clipScrollPosition,
            sectionKey: "clip");

        EditorGUILayout.Space(10f);
        DrawSummaryAndBake();
        DrawBakeResults();

        EditorGUILayout.EndScrollView();
    }

    private void DrawHeader()
    {
        EditorGUILayout.LabelField("Batch Bake Animation To Sprite Sheet", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Select usable prefabs and clips directly from filtered lists. " +
            "The baker samples every selected clip and imports the result as a sliced Unity sprite sheet.",
            MessageType.Info);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Refresh Assets", GUILayout.Width(120f)))
            {
                RefreshAssetCaches();
            }

            GUILayout.FlexibleSpace();
            EditorGUILayout.LabelField(
                $"Usable Prefabs: {prefabItems.Count}    Clips: {clipItems.Count}",
                EditorStyles.miniLabel,
                GUILayout.ExpandWidth(false));
        }
    }

    private void DrawSettings()
    {
        EditorGUILayout.LabelField("Bake Settings", EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        framesPerClip = EditorGUILayout.IntField("Output Frames", framesPerClip);
        EditorGUILayout.HelpBox(
            "This controls how many interpolated sprite cells are baked for each selected clip.",
            MessageType.None);
        outputWidth = EditorGUILayout.IntField("Output Width", outputWidth);
        outputHeight = EditorGUILayout.IntField("Output Height", outputHeight);
        boundsPaddingPercent = EditorGUILayout.Slider("Bounds Padding", boundsPaddingPercent, 0f, 0.5f);
        includeInactiveChildren = EditorGUILayout.Toggle("Include Inactive Children", includeInactiveChildren);
        overwriteExistingFiles = EditorGUILayout.Toggle("Overwrite Existing", overwriteExistingFiles);
        showOnlyLikelyBakeablePrefabs = EditorGUILayout.Toggle("Show Only Likely Bakeable Prefabs", showOnlyLikelyBakeablePrefabs);
        if (EditorGUI.EndChangeCheck())
        {
            SaveSettings();
            RefreshAssetCaches();
        }

        DrawOutputFolderPicker();
    }

    private void DrawOutputFolderPicker()
    {
        using (new EditorGUILayout.HorizontalScope())
        {
            EditorGUILayout.PrefixLabel("Output Folder");

            if (GUILayout.Button("Browse...", GUILayout.Width(90f)))
            {
                BrowseOutputFolder();
                SaveSettings();
            }

            if (GUILayout.Button("Default", GUILayout.Width(70f)))
            {
                outputFolder = null;
                SaveSettings();
            }
        }

        string effectiveAssetPath = ResolveOutputFolderPath();
        string effectiveAbsolutePath = ToAbsolutePath(effectiveAssetPath);

        EditorGUILayout.SelectableLabel(
            effectiveAbsolutePath,
            EditorStyles.textField,
            GUILayout.Height(EditorGUIUtility.singleLineHeight));

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Open Output Folder", GUILayout.Width(150f)))
            {
                EditorUtility.RevealInFinder(effectiveAbsolutePath);
            }

            using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(lastBakedFolderAssetPath)))
            {
                if (GUILayout.Button("Open Last Baked Folder", GUILayout.Width(170f)))
                {
                    EditorUtility.RevealInFinder(ToAbsolutePath(lastBakedFolderAssetPath));
                }
            }
        }

        EditorGUILayout.HelpBox(
            "Output folder must stay inside this project's Assets folder so the baked PNG files can be imported as Sprites automatically.",
            MessageType.None);
    }

    private void BrowseOutputFolder()
    {
        string initialAbsolutePath = ToAbsolutePath(ResolveOutputFolderPath());
        string selectedAbsolutePath = EditorUtility.OpenFolderPanel(
            "Select Output Folder (must be inside Assets)",
            initialAbsolutePath,
            string.Empty);

        if (string.IsNullOrEmpty(selectedAbsolutePath))
        {
            return;
        }

        string assetsAbsolutePath = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
        string normalizedSelectedPath = Path.GetFullPath(selectedAbsolutePath).Replace('\\', '/');

        if (!normalizedSelectedPath.StartsWith(assetsAbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            EditorUtility.DisplayDialog(
                "Invalid Output Folder",
                "Please choose a folder inside this project's Assets directory.",
                "OK");
            return;
        }

        string relativeAssetPath = "Assets" + normalizedSelectedPath.Substring(assetsAbsolutePath.Length);
        relativeAssetPath = relativeAssetPath.Replace('\\', '/');

        EnsureAssetFolderExists(relativeAssetPath);
        outputFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(relativeAssetPath);
    }

    private void LoadSettings()
    {
        framesPerClip = EditorPrefs.GetInt(EditorPrefsPrefix + "FramesPerClip", framesPerClip);
        outputWidth = EditorPrefs.GetInt(EditorPrefsPrefix + "OutputWidth", outputWidth);
        outputHeight = EditorPrefs.GetInt(EditorPrefsPrefix + "OutputHeight", outputHeight);
        boundsPaddingPercent = EditorPrefs.GetFloat(EditorPrefsPrefix + "BoundsPaddingPercent", boundsPaddingPercent);
        includeInactiveChildren = EditorPrefs.GetBool(EditorPrefsPrefix + "IncludeInactiveChildren", includeInactiveChildren);
        overwriteExistingFiles = EditorPrefs.GetBool(EditorPrefsPrefix + "OverwriteExistingFiles", overwriteExistingFiles);
        showOnlyLikelyBakeablePrefabs = EditorPrefs.GetBool(EditorPrefsPrefix + "ShowOnlyLikelyBakeablePrefabs", showOnlyLikelyBakeablePrefabs);

        string outputFolderPath = EditorPrefs.GetString(EditorPrefsPrefix + "OutputFolder", string.Empty);
        if (!string.IsNullOrEmpty(outputFolderPath) && AssetDatabase.IsValidFolder(outputFolderPath))
        {
            outputFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(outputFolderPath);
        }
    }

    private void SaveSettings()
    {
        EditorPrefs.SetInt(EditorPrefsPrefix + "FramesPerClip", framesPerClip);
        EditorPrefs.SetInt(EditorPrefsPrefix + "OutputWidth", outputWidth);
        EditorPrefs.SetInt(EditorPrefsPrefix + "OutputHeight", outputHeight);
        EditorPrefs.SetFloat(EditorPrefsPrefix + "BoundsPaddingPercent", boundsPaddingPercent);
        EditorPrefs.SetBool(EditorPrefsPrefix + "IncludeInactiveChildren", includeInactiveChildren);
        EditorPrefs.SetBool(EditorPrefsPrefix + "OverwriteExistingFiles", overwriteExistingFiles);
        EditorPrefs.SetBool(EditorPrefsPrefix + "ShowOnlyLikelyBakeablePrefabs", showOnlyLikelyBakeablePrefabs);
        EditorPrefs.SetString(EditorPrefsPrefix + "OutputFolder", IsValidAssetFolder(outputFolder) ? AssetDatabase.GetAssetPath(outputFolder) : string.Empty);
    }

    private void DrawSelectionArea<T>(
        string title,
        string helpText,
        List<AssetSelectionItem<T>> items,
        FolderNode<T> root,
        HashSet<string> selectedGuids,
        ref string searchText,
        ref Vector2 scroll,
        string sectionKey) where T : UnityEngine.Object
    {
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(helpText, MessageType.None);

        using (new EditorGUILayout.HorizontalScope())
        {
            searchText = EditorGUILayout.TextField("Search", searchText);

            if (GUILayout.Button("All Visible", GUILayout.Width(90f)))
            {
                SelectVisibleItems(root, selectedGuids, searchText);
            }

            if (GUILayout.Button("None Visible", GUILayout.Width(90f)))
            {
                DeselectVisibleItems(root, selectedGuids, searchText);
            }
        }

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(280f));

            if (root == null)
            {
                EditorGUILayout.LabelField("No assets found.");
            }
            else
            {
                DrawFolderNode(root, selectedGuids, searchText, sectionKey, isRoot: true);
            }

            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawSummaryAndBake()
    {
        int prefabCount = selectedPrefabGuids.Count;
        int clipCount = selectedClipGuids.Count;
        int totalJobs = prefabCount * clipCount;

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            EditorGUILayout.LabelField("Selection Summary", EditorStyles.boldLabel);
            EditorGUILayout.LabelField($"Selected Prefabs: {prefabCount}");
            EditorGUILayout.LabelField($"Selected Clips: {clipCount}");
        EditorGUILayout.LabelField($"Bake Jobs: {totalJobs}");
        }

        bool canBake = CanBake(prefabCount, clipCount);
        using (new EditorGUI.DisabledScope(!canBake))
        {
            if (GUILayout.Button("Bake Selected", GUILayout.Height(34f)))
            {
                BakeSelected();
            }
        }

        if (!canBake)
        {
            EditorGUILayout.HelpBox(
                "Select at least one usable prefab and one animation clip before baking.",
                MessageType.Warning);
        }
    }

    private bool CanBake(int prefabCount, int clipCount)
    {
        return prefabCount > 0 &&
               clipCount > 0 &&
               framesPerClip > 0 &&
               outputWidth > 0 &&
               outputHeight > 0;
    }

    private void RefreshAssetCaches()
    {
        prefabItems = LoadUsablePrefabs();
        clipItems = LoadAnimationClips();
        prefabRoot = BuildFolderTree(prefabItems);
        clipRoot = BuildFolderTree(clipItems);
        RemoveInvalidSelections();
    }

    private void RemoveInvalidSelections()
    {
        selectedPrefabGuids.RemoveWhere(guid => prefabItems.All(item => item.Guid != guid));
        selectedClipGuids.RemoveWhere(guid => clipItems.All(item => item.Guid != guid));
    }

    private void SelectVisibleItems<T>(FolderNode<T> root, HashSet<string> selectedGuids, string searchText) where T : UnityEngine.Object
    {
        foreach (AssetSelectionItem<T> item in GetVisibleItems(root, searchText))
        {
            selectedGuids.Add(item.Guid);
        }
    }

    private void DeselectVisibleItems<T>(FolderNode<T> root, HashSet<string> selectedGuids, string searchText) where T : UnityEngine.Object
    {
        foreach (AssetSelectionItem<T> item in GetVisibleItems(root, searchText))
        {
            selectedGuids.Remove(item.Guid);
        }
    }

    private IEnumerable<AssetSelectionItem<T>> GetVisibleItems<T>(FolderNode<T> node, string searchText) where T : UnityEngine.Object
    {
        if (node == null)
        {
            yield break;
        }

        foreach (AssetSelectionItem<T> item in node.Items)
        {
            if (MatchesSearch(item, searchText))
            {
                yield return item;
            }
        }

        foreach (FolderNode<T> child in node.Children)
        {
            foreach (AssetSelectionItem<T> item in GetVisibleItems(child, searchText))
            {
                yield return item;
            }
        }
    }

    private void DrawFolderNode<T>(
        FolderNode<T> node,
        HashSet<string> selectedGuids,
        string searchText,
        string sectionKey,
        bool isRoot = false) where T : UnityEngine.Object
    {
        if (node == null)
        {
            return;
        }

        List<AssetSelectionItem<T>> visibleDescendants = GetVisibleItems(node, searchText).ToList();
        if (!isRoot && visibleDescendants.Count == 0)
        {
            return;
        }

        if (!isRoot)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool allSelected = visibleDescendants.Count > 0 && visibleDescendants.All(item => selectedGuids.Contains(item.Guid));
                bool anySelected = visibleDescendants.Any(item => selectedGuids.Contains(item.Guid));
                EditorGUI.showMixedValue = anySelected && !allSelected;
                bool toggleValue = EditorGUILayout.Toggle(allSelected, GUILayout.Width(18f));
                EditorGUI.showMixedValue = false;

                if (toggleValue != allSelected)
                {
                    SetSelection(visibleDescendants, selectedGuids, toggleValue);
                }

                string foldoutKey = $"{sectionKey}:{node.Path}";
                bool expanded = GetFoldoutState(foldoutKey, true);
                bool nextExpanded = EditorGUILayout.Foldout(expanded, $"{node.Name} ({visibleDescendants.Count})", true);
                SetFoldoutState(foldoutKey, nextExpanded);
            }
        }

        if (!isRoot && !GetFoldoutState($"{sectionKey}:{node.Path}", true))
        {
            return;
        }

        EditorGUI.indentLevel++;

        foreach (AssetSelectionItem<T> item in node.Items)
        {
            if (!MatchesSearch(item, searchText))
            {
                continue;
            }

            bool isSelected = selectedGuids.Contains(item.Guid);
            string label = BuildItemLabel(item);
            bool nextSelected = EditorGUILayout.ToggleLeft(label, isSelected);
            if (nextSelected != isSelected)
            {
                if (nextSelected)
                {
                    selectedGuids.Add(item.Guid);
                }
                else
                {
                    selectedGuids.Remove(item.Guid);
                }
            }
        }

        foreach (FolderNode<T> child in node.Children.OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase))
        {
            DrawFolderNode(child, selectedGuids, searchText, sectionKey);
        }

        EditorGUI.indentLevel--;
    }

    private static void SetSelection<T>(IEnumerable<AssetSelectionItem<T>> items, HashSet<string> selectedGuids, bool selected)
        where T : UnityEngine.Object
    {
        foreach (AssetSelectionItem<T> item in items)
        {
            if (selected)
            {
                selectedGuids.Add(item.Guid);
            }
            else
            {
                selectedGuids.Remove(item.Guid);
            }
        }
    }

    private bool GetFoldoutState(string key, bool defaultValue)
    {
        if (!foldoutStates.TryGetValue(key, out bool expanded))
        {
            foldoutStates[key] = defaultValue;
            return defaultValue;
        }

        return expanded;
    }

    private void SetFoldoutState(string key, bool value)
    {
        foldoutStates[key] = value;
    }

    private static bool MatchesSearch<T>(AssetSelectionItem<T> item, string searchText) where T : UnityEngine.Object
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        return item.Name.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0 ||
               item.AssetPath.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private List<AssetSelectionItem<GameObject>> LoadUsablePrefabs()
    {
        var results = new List<AssetSelectionItem<GameObject>>();
        string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (prefab == null)
            {
                continue;
            }

            if (showOnlyLikelyBakeablePrefabs && !IsLikelyBakeablePrefab(prefab))
            {
                continue;
            }

            results.Add(new AssetSelectionItem<GameObject>(guid, path, prefab));
        }

        return results
            .OrderBy(item => item.AssetPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private List<AssetSelectionItem<AnimationClip>> LoadAnimationClips()
    {
        var results = new List<AssetSelectionItem<AnimationClip>>();
        string[] guids = AssetDatabase.FindAssets("t:AnimationClip", new[] { "Assets" });

        foreach (string guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (clip == null || clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            results.Add(new AssetSelectionItem<AnimationClip>(guid, path, clip));
        }

        return results
            .OrderBy(item => item.AssetPath, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsLikelyBakeablePrefab(GameObject prefab)
    {
        Animator animator = prefab.GetComponentInChildren<Animator>(true);
        Renderer[] renderers = prefab.GetComponentsInChildren<Renderer>(true);
        bool hasRenderable = renderers.Any(renderer => renderer != null && !(renderer is ParticleSystemRenderer));
        bool hasAnimator = animator != null;
        bool hasController = animator != null && animator.runtimeAnimatorController != null;

        return hasRenderable && (hasController || hasAnimator);
    }

    private static FolderNode<T> BuildFolderTree<T>(List<AssetSelectionItem<T>> items) where T : UnityEngine.Object
    {
        var root = new FolderNode<T>("Assets", "Assets");

        foreach (AssetSelectionItem<T> item in items)
        {
            string directoryPath = Path.GetDirectoryName(item.AssetPath)?.Replace('\\', '/') ?? "Assets";
            string[] segments = directoryPath.Split('/');
            FolderNode<T> current = root;
            string currentPath = "Assets";

            for (int i = 1; i < segments.Length; i++)
            {
                currentPath += "/" + segments[i];
                FolderNode<T> child = current.Children.FirstOrDefault(node => node.Path == currentPath);
                if (child == null)
                {
                    child = new FolderNode<T>(segments[i], currentPath);
                    current.Children.Add(child);
                }

                current = child;
            }

            current.Items.Add(item);
        }

        SortFolderTree(root);
        return root;
    }

    private static void SortFolderTree<T>(FolderNode<T> node) where T : UnityEngine.Object
    {
        node.Items.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        node.Children = node.Children
            .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (FolderNode<T> child in node.Children)
        {
            SortFolderTree(child);
        }
    }

    private void BakeSelected()
    {
        if (framesPerClip <= 0)
        {
            EditorUtility.DisplayDialog("Invalid Frame Count", "Output Frames must be greater than 0.", "OK");
            return;
        }

        if (outputWidth <= 0 || outputHeight <= 0)
        {
            EditorUtility.DisplayDialog("Invalid Size", "Output width and height must be greater than 0.", "OK");
            return;
        }

        string outputFolderPath = ResolveOutputFolderPath();
        Directory.CreateDirectory(ToAbsolutePath(outputFolderPath));

        List<GameObject> selectedPrefabs = prefabItems
            .Where(item => selectedPrefabGuids.Contains(item.Guid))
            .Select(item => item.Asset)
            .ToList();

        List<AnimationClip> selectedClips = clipItems
            .Where(item => selectedClipGuids.Contains(item.Guid))
            .Select(item => item.Asset)
            .ToList();

        int totalJobs = selectedPrefabs.Count * selectedClips.Count;
        int completedJobs = 0;
        int skippedJobs = 0;
        List<string> skippedReasons = new List<string>();
        bakeResults.Clear();

        try
        {
            foreach (GameObject prefab in selectedPrefabs)
            {
                foreach (AnimationClip clip in selectedClips)
                {
                    completedJobs++;
                    float progress = totalJobs <= 0 ? 1f : completedJobs / (float)totalJobs;
                    EditorUtility.DisplayProgressBar(
                        "Baking PNG Sequences",
                        $"{prefab.name} / {clip.name}",
                        progress);

                    if (!BakeSinglePrefabClip(prefab, clip, outputFolderPath, out BakeResult result))
                    {
                        skippedJobs++;
                        string message = $"Skipped bake for '{prefab.name}' / '{clip.name}': {result.Message}";
                        skippedReasons.Add(message);
                        Debug.LogWarning(message);
                    }

                    bakeResults.Add(result);
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();
        }

        EditorUtility.DisplayDialog(
            "Bake Complete",
            BuildBakeCompleteMessage(completedJobs, skippedJobs, skippedReasons),
            "OK");
    }

    private void DrawBakeResults()
    {
        if (bakeResults.Count == 0)
        {
            return;
        }

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Last Bake Results", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            resultScrollPosition = EditorGUILayout.BeginScrollView(resultScrollPosition, GUILayout.Height(180f));

            foreach (BakeResult result in bakeResults)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField(
                        $"{(result.Success ? "OK" : "Skipped")}  {result.PrefabName} / {result.ClipName}",
                        EditorStyles.boldLabel);

                    if (!string.IsNullOrEmpty(result.Message))
                    {
                        EditorGUILayout.LabelField(result.Message, EditorStyles.wordWrappedMiniLabel);
                    }

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(result.OutputFolderAssetPath)))
                        {
                            if (GUILayout.Button("Reveal Folder", GUILayout.Width(110f)))
                            {
                                EditorUtility.RevealInFinder(ToAbsolutePath(result.OutputFolderAssetPath));
                            }
                        }

                        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(result.AtlasAssetPath)))
                        {
                            if (GUILayout.Button("Select Atlas", GUILayout.Width(100f)))
                            {
                                SelectAsset(result.AtlasAssetPath);
                            }
                        }

                        using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(result.AnimationClipAssetPath)))
                        {
                            if (GUILayout.Button("Select Anim", GUILayout.Width(100f)))
                            {
                                SelectAsset(result.AnimationClipAssetPath);
                            }
                        }
                    }
                }
            }

            EditorGUILayout.EndScrollView();
        }
    }

    private static string BuildBakeCompleteMessage(int attemptedJobs, int skippedJobs, List<string> skippedReasons)
    {
        string message = $"Attempted {attemptedJobs} clip jobs. Skipped {skippedJobs} invalid or empty jobs.";
        if (skippedReasons == null || skippedReasons.Count == 0)
        {
            return message;
        }

        int maxReasons = Mathf.Min(3, skippedReasons.Count);
        for (int i = 0; i < maxReasons; i++)
        {
            message += "\n\n" + skippedReasons[i];
        }

        if (skippedReasons.Count > maxReasons)
        {
            message += $"\n\n...and {skippedReasons.Count - maxReasons} more. See Console for details.";
        }

        return message;
    }

    private bool BakeSinglePrefabClip(GameObject prefab, AnimationClip clip, string outputFolderPath, out BakeResult result)
    {
        result = new BakeResult
        {
            PrefabName = prefab != null ? prefab.name : string.Empty,
            ClipName = clip != null ? clip.name : string.Empty
        };

        GameObject instance = null;
        Camera bakeCamera = null;
        RenderTexture renderTexture = null;
        Texture2D captureTexture = null;

        try
        {
            instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
            if (instance == null)
            {
                result.Message = "Failed to instantiate prefab.";
                return false;
            }

            instance.hideFlags = HideFlags.HideAndDontSave;
            instance.transform.position = Vector3.zero;
            instance.transform.rotation = Quaternion.identity;
            instance.transform.localScale = Vector3.one;
            int bakeLayer = ResolveUnusedSceneLayer();
            SetLayerRecursively(instance, bakeLayer);

            if (!TryResolveSampleRoot(instance, clip, out Transform sampleRoot, out string skipReason))
            {
                result.Message = skipReason;
                return false;
            }

            Animator animator = instance.GetComponentInChildren<Animator>(includeInactiveChildren);
            if (animator != null)
            {
                animator.enabled = false;
                animator.Rebind();
                animator.Update(0f);
            }

            int frameCount = Mathf.Max(1, framesPerClip);
            List<float> sampleTimes = BuildSampleTimes(clip, frameCount);

            AnimationMode.StartAnimationMode();
            Bounds combinedBounds = CalculateCombinedBounds(sampleRoot.gameObject, clip, sampleTimes, instance);

            if (!combinedBounds.size.IsFinite() || combinedBounds.size == Vector3.zero)
            {
                result.Message = "Sampled clip has no visible renderer bounds on this prefab.";
                return false;
            }

            string clipOutputFolder = $"{outputFolderPath}/{Sanitize(prefab.name)}/{Sanitize(clip.name)}";
            if (overwriteExistingFiles)
            {
                TryDeleteDirectory(clipOutputFolder);
            }

            Directory.CreateDirectory(ToAbsolutePath(clipOutputFolder));

            GameObject cameraRoot = new GameObject("AnimPngBakeCamera");
            cameraRoot.hideFlags = HideFlags.HideAndDontSave;
            bakeCamera = cameraRoot.AddComponent<Camera>();
            bakeCamera.orthographic = true;
            bakeCamera.clearFlags = CameraClearFlags.SolidColor;
            bakeCamera.backgroundColor = Color.clear;
            bakeCamera.nearClipPlane = 0.01f;
            bakeCamera.farClipPlane = 100f;
            bakeCamera.allowHDR = false;
            bakeCamera.allowMSAA = false;
            bakeCamera.cullingMask = 1 << bakeLayer;
            bakeCamera.transform.position = new Vector3(combinedBounds.center.x, combinedBounds.center.y, -10f);
            bakeCamera.transform.rotation = Quaternion.identity;

            float aspect = outputWidth / (float)outputHeight;
            float maxExtent = Mathf.Max(combinedBounds.extents.x, combinedBounds.extents.y);
            float padding = Mathf.Max(0f, maxExtent * boundsPaddingPercent);
            float halfHeight = combinedBounds.extents.y + padding;
            float halfWidth = combinedBounds.extents.x + padding;
            bakeCamera.orthographicSize = Mathf.Max(halfHeight, halfWidth / Mathf.Max(0.0001f, aspect));

            var descriptor = new RenderTextureDescriptor(outputWidth, outputHeight)
            {
                graphicsFormat = GraphicsFormat.R8G8B8A8_UNorm,
                depthBufferBits = 24,
                msaaSamples = 1,
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false
            };

            renderTexture = new RenderTexture(descriptor)
            {
                filterMode = FilterMode.Point
            };
            renderTexture.Create();

            captureTexture = new Texture2D(outputWidth, outputHeight, TextureFormat.RGBA32, false);
            bakeCamera.targetTexture = renderTexture;

            bool foundVisiblePixels = false;
            List<Color32[]> framePixels = new List<Color32[]>(sampleTimes.Count);

            for (int frameIndex = 0; frameIndex < sampleTimes.Count; frameIndex++)
            {
                SampleClip(sampleRoot.gameObject, clip, sampleTimes[frameIndex]);

                RenderTexture previous = RenderTexture.active;
                Graphics.SetRenderTarget(renderTexture);
                GL.Clear(true, true, Color.clear);
                bakeCamera.Render();

                captureTexture.ReadPixels(new Rect(0, 0, outputWidth, outputHeight), 0, 0);
                captureTexture.Apply(false, false);
                RenderTexture.active = previous;

                if (!foundVisiblePixels && HasVisiblePixels(captureTexture))
                {
                    foundVisiblePixels = true;
                }

                framePixels.Add(captureTexture.GetPixels32());
            }

            if (!foundVisiblePixels)
            {
                result.Message = "Rendered frames contained no visible pixels. The prefab and clip are likely incompatible or uninitialized.";
                TryDeleteDirectory(clipOutputFolder);
                return false;
            }

            string safeClipName = Sanitize(clip.name);
            string atlasAssetPath = $"{clipOutputFolder}/{GetAtlasFileName(safeClipName)}";
            string atlasAbsolutePath = ToAbsolutePath(atlasAssetPath);
            if (!overwriteExistingFiles && File.Exists(atlasAbsolutePath))
            {
                result.Message = "Atlas already exists and Overwrite Existing is disabled.";
                return false;
            }

            WriteAtlasPng(atlasAbsolutePath, framePixels, outputWidth, outputHeight, out int atlasColumns, out int atlasRows);
            AssetDatabase.ImportAsset(atlasAssetPath, ImportAssetOptions.ForceUpdate);
            ConfigureImportedAtlas(atlasAssetPath, safeClipName, framePixels.Count, outputWidth, outputHeight, atlasColumns, atlasRows);
            string bakedAnimationAssetPath = CreateBakedAnimationClip(clipOutputFolder, safeClipName, atlasAssetPath, clip, framePixels.Count);

            lastBakedFolderAssetPath = clipOutputFolder;
            result.Success = true;
            result.Message = $"Generated atlas and baked animation with {framePixels.Count} frames.";
            result.OutputFolderAssetPath = clipOutputFolder;
            result.AtlasAssetPath = atlasAssetPath;
            result.AnimationClipAssetPath = bakedAnimationAssetPath;
            return true;
        }
        finally
        {
            if (AnimationMode.InAnimationMode())
            {
                AnimationMode.StopAnimationMode();
            }

            if (captureTexture != null)
            {
                DestroyImmediate(captureTexture);
            }

            if (renderTexture != null)
            {
                if (bakeCamera != null && bakeCamera.targetTexture == renderTexture)
                {
                    bakeCamera.targetTexture = null;
                }

                renderTexture.Release();
                DestroyImmediate(renderTexture);
            }

            if (bakeCamera != null)
            {
                DestroyImmediate(bakeCamera.gameObject);
            }

            if (instance != null)
            {
                DestroyImmediate(instance);
            }

        }
    }

    private bool TryResolveSampleRoot(GameObject instance, AnimationClip clip, out Transform sampleRoot, out string reason)
    {
        sampleRoot = null;
        reason = null;

        HashSet<string> clipPaths = GetClipPaths(clip);
        if (clipPaths.Count == 0)
        {
            reason = "Clip has no animation bindings.";
            return false;
        }

        Transform[] candidates = instance.GetComponentsInChildren<Transform>(includeInactiveChildren);
        int bestMatchCount = -1;
        Transform bestCandidate = null;

        foreach (Transform candidate in candidates)
        {
            HashSet<string> candidatePaths = new HashSet<string>(GetTransformPaths(candidate.gameObject), StringComparer.Ordinal);
            int matchCount = clipPaths.Count(candidatePaths.Contains);

            if (matchCount > bestMatchCount)
            {
                bestMatchCount = matchCount;
                bestCandidate = candidate;
            }
        }

        if (bestCandidate == null || bestMatchCount <= 0)
        {
            reason = "Clip binding paths do not exist on this prefab.";
            return false;
        }

        sampleRoot = bestCandidate;

        if (!HasAssignedSpriteRenderer(sampleRoot.gameObject))
        {
            reason = $"Resolved sample root '{sampleRoot.name}' has no SpriteRenderer with an assigned sprite before baking.";
            return false;
        }

        return true;
    }

    private static List<float> BuildSampleTimes(AnimationClip clip, int frameCount)
    {
        var times = new List<float>(frameCount);
        float safeLength = Mathf.Max(0f, clip.length);

        for (int i = 0; i < frameCount; i++)
        {
            float normalized = frameCount <= 1 ? 0f : i / (float)frameCount;
            times.Add(normalized * safeLength);
        }

        if (times.Count == 0)
        {
            times.Add(0f);
        }

        return times;
    }

    private Bounds CalculateCombinedBounds(GameObject sampleRoot, AnimationClip clip, List<float> sampleTimes, GameObject rendererRoot)
    {
        bool hasBounds = false;
        Bounds combinedBounds = default;

        foreach (float time in sampleTimes)
        {
            SampleClip(sampleRoot, clip, time);
            if (!TryGetRendererBounds(rendererRoot, out Bounds frameBounds))
            {
                continue;
            }

            if (!hasBounds)
            {
                combinedBounds = frameBounds;
                hasBounds = true;
            }
            else
            {
                combinedBounds.Encapsulate(frameBounds.min);
                combinedBounds.Encapsulate(frameBounds.max);
            }
        }

        return combinedBounds;
    }

    private static HashSet<string> GetClipPaths(AnimationClip clip)
    {
        EditorCurveBinding[] floatBindings = AnimationUtility.GetCurveBindings(clip);
        EditorCurveBinding[] objectBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip);
        HashSet<string> clipPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (EditorCurveBinding binding in floatBindings)
        {
            if (!string.IsNullOrEmpty(binding.path))
            {
                clipPaths.Add(binding.path);
            }
        }

        foreach (EditorCurveBinding binding in objectBindings)
        {
            if (!string.IsNullOrEmpty(binding.path))
            {
                clipPaths.Add(binding.path);
            }
        }

        return clipPaths;
    }

    private static string[] GetTransformPaths(GameObject root)
    {
        List<string> paths = new List<string>();
        foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
        {
            string path = AnimationUtility.CalculateTransformPath(transform, root.transform);
            paths.Add(path);
        }

        return paths.ToArray();
    }

    private bool HasAssignedSpriteRenderer(GameObject root)
    {
        SpriteRenderer[] renderers = root.GetComponentsInChildren<SpriteRenderer>(includeInactiveChildren);
        return renderers.Any(renderer => renderer != null && renderer.enabled && renderer.sprite != null && renderer.color.a > 0f);
    }

    private static bool HasVisiblePixels(Texture2D texture)
    {
        Color32[] pixels = texture.GetPixels32();
        if (pixels.Length == 0)
        {
            return false;
        }

        Color32 first = pixels[0];
        bool allSame = true;

        for (int i = 0; i < pixels.Length; i++)
        {
            if (!ColorsEqual(first, pixels[i]))
            {
                allSame = false;
            }

            if (pixels[i].a > 0)
            {
                if (!allSame)
                {
                    return true;
                }
            }
        }

        return !allSame && pixels.Any(pixel => pixel.a > 0);
    }

    private static bool ColorsEqual(Color32 a, Color32 b)
    {
        return a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;
    }

    private static void SampleClip(GameObject instance, AnimationClip clip, float time)
    {
        AnimationMode.BeginSampling();
        AnimationMode.SampleAnimationClip(instance, clip, time);
        AnimationMode.EndSampling();
    }

    private bool TryGetRendererBounds(GameObject root, out Bounds bounds)
    {
        Renderer[] renderers = root.GetComponentsInChildren<Renderer>(includeInactiveChildren);
        bool found = false;
        bounds = default;

        foreach (Renderer renderer in renderers)
        {
            if (!renderer.enabled)
            {
                continue;
            }

            if (renderer is ParticleSystemRenderer)
            {
                continue;
            }

            if (!found)
            {
                bounds = renderer.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(renderer.bounds.min);
                bounds.Encapsulate(renderer.bounds.max);
            }
        }

        return found;
    }

    private string ResolveOutputFolderPath()
    {
        if (IsValidAssetFolder(outputFolder))
        {
            return AssetDatabase.GetAssetPath(outputFolder);
        }

        EnsureAssetFolderExists(DefaultOutputFolder);

        return DefaultOutputFolder;
    }

    private static bool IsValidAssetFolder(DefaultAsset asset)
    {
        if (asset == null)
        {
            return false;
        }

        string path = AssetDatabase.GetAssetPath(asset);
        return !string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path);
    }

    private static void EnsureAssetFolderExists(string assetFolderPath)
    {
        if (AssetDatabase.IsValidFolder(assetFolderPath))
        {
            return;
        }

        string[] segments = assetFolderPath.Split('/');
        string current = segments[0];
        for (int i = 1; i < segments.Length; i++)
        {
            string next = $"{current}/{segments[i]}";
            if (!AssetDatabase.IsValidFolder(next))
            {
                AssetDatabase.CreateFolder(current, segments[i]);
            }

            current = next;
        }
    }

    private static string ToAbsolutePath(string assetPath)
    {
        string projectRoot = Path.GetDirectoryName(Application.dataPath);
        if (string.IsNullOrEmpty(projectRoot))
        {
            throw new InvalidOperationException("Failed to resolve Unity project root path.");
        }

        return Path.GetFullPath(Path.Combine(projectRoot, assetPath));
    }

    private static string Sanitize(string value)
    {
        char[] invalidChars = Path.GetInvalidFileNameChars();
        foreach (char invalidChar in invalidChars)
        {
            value = value.Replace(invalidChar, '_');
        }

        return value.Trim();
    }

    private static string GetAtlasFileName(string safeClipName)
    {
        return $"{safeClipName}{AtlasSuffix}.png";
    }

    private static string GetBakedAnimationFileName(string safeClipName)
    {
        return $"{safeClipName}{BakedAnimationSuffix}.anim";
    }

    private static void SetLayerRecursively(GameObject root, int layer)
    {
        foreach (Transform child in root.GetComponentsInChildren<Transform>(true))
        {
            child.gameObject.layer = layer;
        }
    }

    private static int ResolveUnusedSceneLayer()
    {
        bool[] usedLayers = new bool[32];

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                continue;
            }

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform transform in root.GetComponentsInChildren<Transform>(true))
                {
                    usedLayers[transform.gameObject.layer] = true;
                }
            }
        }

        for (int layer = 31; layer >= 0; layer--)
        {
            if (!usedLayers[layer])
            {
                return layer;
            }
        }

        return FallbackBakeLayer;
    }

    private static void WriteAtlasPng(
        string absolutePath,
        List<Color32[]> framePixels,
        int frameWidth,
        int frameHeight,
        out int columns,
        out int rows)
    {
        int frameCount = Mathf.Max(1, framePixels.Count);
        columns = Mathf.CeilToInt(Mathf.Sqrt(frameCount));
        rows = Mathf.CeilToInt(frameCount / (float)columns);

        int atlasWidth = columns * frameWidth;
        int atlasHeight = rows * frameHeight;
        Texture2D atlas = new Texture2D(atlasWidth, atlasHeight, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp
        };

        Color32[] clearPixels = Enumerable.Repeat(new Color32(0, 0, 0, 0), atlasWidth * atlasHeight).ToArray();
        atlas.SetPixels32(clearPixels);

        for (int frameIndex = 0; frameIndex < framePixels.Count; frameIndex++)
        {
            int column = frameIndex % columns;
            int row = frameIndex / columns;
            int x = column * frameWidth;
            int y = (rows - 1 - row) * frameHeight;
            atlas.SetPixels32(x, y, frameWidth, frameHeight, framePixels[frameIndex]);
        }

        atlas.Apply(false, false);
        File.WriteAllBytes(absolutePath, atlas.EncodeToPNG());
        DestroyImmediate(atlas);
    }

    private static void ConfigureImportedAtlas(
        string assetPath,
        string clipName,
        int frameCount,
        int frameWidth,
        int frameHeight,
        int columns,
        int rows)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null)
        {
            return;
        }

        importer.textureType = TextureImporterType.Sprite;
        importer.spriteImportMode = SpriteImportMode.Multiple;
        importer.alphaIsTransparency = true;
        importer.mipmapEnabled = false;
        importer.filterMode = FilterMode.Point;
        importer.wrapMode = TextureWrapMode.Clamp;
        int atlasMaxSize = Mathf.NextPowerOfTwo(Mathf.Max(columns * frameWidth, rows * frameHeight));
        importer.maxTextureSize = Mathf.Clamp(atlasMaxSize, 32, 8192);

        SpriteMetaData[] spriteMetaData = new SpriteMetaData[frameCount];
        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            int column = frameIndex % columns;
            int row = frameIndex / columns;
            int x = column * frameWidth;
            int y = (rows - 1 - row) * frameHeight;

            spriteMetaData[frameIndex] = new SpriteMetaData
            {
                name = $"{clipName}_{frameIndex + 1:D4}",
                rect = new Rect(x, y, frameWidth, frameHeight),
                alignment = (int)SpriteAlignment.Center,
                pivot = new Vector2(0.5f, 0.5f)
            };
        }

        importer.spritesheet = spriteMetaData;
        importer.SaveAndReimport();
    }

    private static string CreateBakedAnimationClip(
        string outputFolderAssetPath,
        string safeClipName,
        string atlasAssetPath,
        AnimationClip sourceClip,
        int frameCount)
    {
        Sprite[] sprites = AssetDatabase.LoadAllAssetRepresentationsAtPath(atlasAssetPath)
            .OfType<Sprite>()
            .OrderBy(sprite => sprite.name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (sprites.Length == 0)
        {
            Debug.LogWarning($"No sliced sprites found in atlas '{atlasAssetPath}'. Baked animation clip was not generated.");
            return string.Empty;
        }

        int keyframeCount = Mathf.Min(frameCount, sprites.Length);
        float sourceClipLength = sourceClip != null ? sourceClip.length : 0f;
        float safeLength = Mathf.Max(sourceClipLength, 1f / Mathf.Max(1, keyframeCount));
        var keyframes = new ObjectReferenceKeyframe[keyframeCount];

        for (int i = 0; i < keyframeCount; i++)
        {
            keyframes[i] = new ObjectReferenceKeyframe
            {
                time = i * safeLength / keyframeCount,
                value = sprites[i]
            };
        }

        var bakedClip = new AnimationClip
        {
            frameRate = keyframeCount / safeLength
        };

        var binding = new EditorCurveBinding
        {
            path = string.Empty,
            type = typeof(SpriteRenderer),
            propertyName = "m_Sprite"
        };
        AnimationUtility.SetObjectReferenceCurve(bakedClip, binding, keyframes);

        if (sourceClip != null)
        {
            AnimationClipSettings sourceSettings = AnimationUtility.GetAnimationClipSettings(sourceClip);
            AnimationUtility.SetAnimationClipSettings(bakedClip, sourceSettings);
        }

        string bakedAnimationAssetPath = $"{outputFolderAssetPath}/{GetBakedAnimationFileName(safeClipName)}";
        AssetDatabase.CreateAsset(bakedClip, bakedAnimationAssetPath);
        AssetDatabase.ImportAsset(bakedAnimationAssetPath, ImportAssetOptions.ForceUpdate);
        return bakedAnimationAssetPath;
    }

    private static void ConfigureImportedPngAsSprite(string assetPath)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null)
        {
            return;
        }

        bool dirty = false;

        if (importer.textureType != TextureImporterType.Sprite)
        {
            importer.textureType = TextureImporterType.Sprite;
            dirty = true;
        }

        if (!importer.alphaIsTransparency)
        {
            importer.alphaIsTransparency = true;
            dirty = true;
        }

        if (importer.mipmapEnabled)
        {
            importer.mipmapEnabled = false;
            dirty = true;
        }

        if (importer.filterMode != FilterMode.Point)
        {
            importer.filterMode = FilterMode.Point;
            dirty = true;
        }

        if (importer.wrapMode != TextureWrapMode.Clamp)
        {
            importer.wrapMode = TextureWrapMode.Clamp;
            dirty = true;
        }

        if (dirty)
        {
            importer.SaveAndReimport();
        }
    }

    private static void TryDeleteDirectory(string assetPath)
    {
        string absolutePath = ToAbsolutePath(assetPath);
        if (Directory.Exists(absolutePath))
        {
            Directory.Delete(absolutePath, true);
            File.Delete(absolutePath + ".meta");
        }
    }

    private static string BuildItemLabel<T>(AssetSelectionItem<T> item) where T : UnityEngine.Object
    {
        string folder = Path.GetDirectoryName(item.AssetPath)?.Replace('\\', '/');
        return string.IsNullOrEmpty(folder) ? item.Name : $"{item.Name}    [{folder}]";
    }

    private static void SelectAsset(string assetPath)
    {
        UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(assetPath);
        if (asset == null)
        {
            return;
        }

        Selection.activeObject = asset;
        EditorGUIUtility.PingObject(asset);
    }

    [Serializable]
    private class BakeResult
    {
        public bool Success;
        public string PrefabName;
        public string ClipName;
        public string Message;
        public string OutputFolderAssetPath;
        public string AtlasAssetPath;
        public string AnimationClipAssetPath;
    }

    [Serializable]
    private class AssetSelectionItem<T> where T : UnityEngine.Object
    {
        public string Guid { get; }
        public string AssetPath { get; }
        public string Name { get; }
        public T Asset { get; }

        public AssetSelectionItem(string guid, string assetPath, T asset)
        {
            Guid = guid;
            AssetPath = assetPath;
            Name = asset != null ? asset.name : Path.GetFileNameWithoutExtension(assetPath);
            Asset = asset;
        }
    }

    [Serializable]
    private class FolderNode<T> where T : UnityEngine.Object
    {
        public string Name;
        public string Path;
        public List<FolderNode<T>> Children = new();
        public List<AssetSelectionItem<T>> Items = new();

        public FolderNode(string name, string path)
        {
            Name = name;
            Path = path;
        }
    }
}

public static class AnimationToPngSequenceBakerBoundsExtensions
{
    public static bool IsFinite(this Vector3 value)
    {
        return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }
}
