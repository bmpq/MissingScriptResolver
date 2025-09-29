using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System;

public class MissingScriptResolver : EditorWindow
{
    private class BrokenReference
    {
        public GameObject Owner;
        public string FilePath;
        public ulong ComponentFileID;
        public string BrokenGuid;
        public string SerializedDataPreview;
        public List<string> SerializedFieldNames = new List<string>();
        public List<ScriptCandidate> Candidates = new List<ScriptCandidate>();
        public MonoScript NewScript;
        public bool WasOdinSerialized;
    }

    private class ScriptCandidate
    {
        public MonoScript Script;
        public List<string> MatchedFields = new List<string>();
        public List<string> UnmatchedFields = new List<string>();
        public int MatchCount => MatchedFields.Count;
        public bool IsOdinScript;
    }

    private class ScriptCacheInfo
    {
        public List<string> FieldNames;
        public bool IsOdinScript;
    }

    private List<BrokenReference> brokenReferences = new List<BrokenReference>();
    private Vector2 scrollPosition;
    private Dictionary<string, bool> candidateFoldouts = new Dictionary<string, bool>();

    private static Dictionary<MonoScript, ScriptCacheInfo> scriptFieldCache;
    private static bool isCacheBuilt = false;

    private BrokenReference _referenceToFix = null;

    private static readonly HashSet<string> ignoredUnityFields = new HashSet<string>
    {
        "m_Name",
        "m_EditorClassIdentifier"
    };

    private static readonly HashSet<string> odinSerializerFields = new HashSet<string>
    {
        "SerializedFormat",
        "SerializedBytes",
        "ReferencedUnityObjects",
        "SerializedBytesString",
        "Prefab",
        "PrefabModificationsReferencedUnityObjects",
        "PrefabModifications",
        "SerializationNodes",
        "serializationData"
    };

    [MenuItem("Tools/Missing Script Resolver")]
    public static void ShowWindow()
    {
        GetWindow<MissingScriptResolver>("Missing Script Resolver");
    }

    private void OnEnable()
    {
        Selection.selectionChanged += OnSelectionChanged;
        OnSelectionChanged(); // Initial run
    }

    private void OnDisable()
    {
        Selection.selectionChanged -= OnSelectionChanged;
    }

    private void OnSelectionChanged()
    {
        FindBrokenReferencesInSelectionAndChildren();
        if (brokenReferences.Count > 0)
        {
            FindAndRankCandidatesForAll();
        }
        Repaint();
    }

    private void FindBrokenReferencesInSelectionAndChildren()
    {
        brokenReferences.Clear();
        candidateFoldouts.Clear();

        var gameObjectsToScan = new HashSet<GameObject>();
        foreach (var go in Selection.gameObjects)
        {
            if (go == null) continue;
            foreach (var transform in go.GetComponentsInChildren<Transform>(true))
            {
                gameObjectsToScan.Add(transform.gameObject);
            }
        }

        if (gameObjectsToScan.Count == 0) return;

        // Group GameObjects by their file path to read each file only once
        var groupedByFile = gameObjectsToScan
            .Where(g => !string.IsNullOrEmpty(GetFilePathForGameObject(g)))
            .GroupBy(g => GetFilePathForGameObject(g));

        foreach (var group in groupedByFile)
        {
            string filePath = group.Key;
            if (!File.Exists(filePath)) continue;

            string[] allLines = File.ReadAllLines(filePath);
            foreach (var go in group)
            {
                FindBrokenReferencesForGameObject(go, filePath, allLines);
            }
        }
    }

    private void FindBrokenReferencesForGameObject(GameObject go, string filePath, string[] allLines)
    {
        ulong targetGoFileID = GetFileID(go);
        if (targetGoFileID == 0) return;

        for (int i = 0; i < allLines.Length; i++)
        {
            // Find a MonoBehaviour component block
            if (allLines[i].StartsWith("--- !u!114 &"))
            {
                ulong componentFileID = ulong.Parse(Regex.Match(allLines[i], @"&(-?\d+)").Groups[1].Value);

                ulong gameObjectFileID = 0;
                string scriptGuid = null;
                long scriptFileID = 0;
                int dataStartIndex = -1;

                // Parse the component block
                for (int j = i + 1; j < allLines.Length && !allLines[j].StartsWith("--- !"); j++)
                {
                    if (allLines[j].Contains("m_GameObject:"))
                    {
                        var match = Regex.Match(allLines[j], @"fileID: (-?\d+)");
                        if (match.Success) gameObjectFileID = ulong.Parse(match.Groups[1].Value);
                    }
                    else if (allLines[j].Contains("m_Script:"))
                    {
                        var scriptMatch = Regex.Match(allLines[j], @"fileID: (-?\d+), guid: ([a-f0-9]{32})");
                        if (scriptMatch.Success)
                        {
                            scriptFileID = long.Parse(scriptMatch.Groups[1].Value);
                            scriptGuid = scriptMatch.Groups[2].Value;
                        }
                        dataStartIndex = j + 1;
                    }
                }

                // Check if this component belongs to our target GameObject and its script is missing
                if (gameObjectFileID == targetGoFileID && !string.IsNullOrEmpty(scriptGuid) &&
                    IsScriptReferenceBroken(scriptGuid, scriptFileID))
                {
                    var reference = new BrokenReference
                    {
                        Owner = go,
                        FilePath = filePath,
                        ComponentFileID = componentFileID,
                        BrokenGuid = scriptGuid,
                        WasOdinSerialized = false
                    };

                    // Extract serialized fields for analysis
                    StringBuilder dataPreview = new StringBuilder();
                    Regex fieldRegex = new Regex(@"^\s+([a-zA-Z_]\w*):"); // Matches lines like "  _myField:"
                    for (int k = dataStartIndex; k < allLines.Length && !allLines[k].StartsWith("--- !"); k++)
                    {
                        string line = allLines[k];
                        if (!string.IsNullOrWhiteSpace(line) && line.StartsWith("  "))
                        {
                            dataPreview.AppendLine(line.Trim());
                            var match = fieldRegex.Match(line);
                            if (match.Success)
                            {
                                string fieldName = match.Groups[1].Value;
                                if (ignoredUnityFields.Contains(fieldName))
                                {
                                    continue; // Skip Unity internal fields
                                }
                                if (odinSerializerFields.Contains(fieldName))
                                {
                                    reference.WasOdinSerialized = true;
                                    continue; // Note that it was an Odin script, but don't add field to match list
                                }

                                reference.SerializedFieldNames.Add(fieldName);
                            }
                        }
                    }
                    reference.SerializedDataPreview = dataPreview.ToString();
                    brokenReferences.Add(reference);
                }
            }
        }
    }

    private static bool IsScriptReferenceBroken(string guid, long fileID)
    {
        string path = AssetDatabase.GUIDToAssetPath(guid);
        if (string.IsNullOrEmpty(path))
        {
            return true; // The script file itself is missing.
        }

        // The script file exists, but the specific class (identified by fileID) is missing from it
        var assets = AssetDatabase.LoadAllAssetsAtPath(path);
        foreach (var asset in assets)
        {
            if (asset is MonoScript)
            {
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out _, out long localFileID))
                {
                    if (localFileID == fileID)
                    {
                        return false;
                    }
                }
            }
        }

        return true;
    }

    private void FindAndRankCandidatesForAll()
    {
        if (!isCacheBuilt)
        {
            BuildScriptCache();
        }

        foreach (var reference in brokenReferences)
        {
            reference.Candidates.Clear();
            if (reference.SerializedFieldNames.Count == 0 && !reference.WasOdinSerialized) continue;

            foreach (var cacheEntry in scriptFieldCache)
            {
                var script = cacheEntry.Key;
                var scriptInfo = cacheEntry.Value;

                var matchedFields = new List<string>();
                var unmatchedFields = new List<string>();

                foreach (var fieldName in reference.SerializedFieldNames)
                {
                    if (scriptInfo.FieldNames.Contains(fieldName))
                    {
                        matchedFields.Add(fieldName);
                    }
                    else
                    {
                        unmatchedFields.Add(fieldName);
                    }
                }

                if (matchedFields.Count > 0)
                {
                    reference.Candidates.Add(new ScriptCandidate
                    {
                        Script = script,
                        MatchedFields = matchedFields,
                        UnmatchedFields = unmatchedFields,
                        IsOdinScript = scriptInfo.IsOdinScript
                    });
                }
            }

            reference.Candidates.Sort((a, b) =>
            {
                if (reference.WasOdinSerialized)
                {
                    int odinCompare = b.IsOdinScript.CompareTo(a.IsOdinScript);
                    if (odinCompare != 0) return odinCompare;
                }
                return b.MatchCount.CompareTo(a.MatchCount);
            });
        }
    }

    private void OnGUI()
    {
        EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Rebuild Script Cache", EditorStyles.toolbarButton))
        {
            BuildScriptCache();
            OnSelectionChanged();
        }
        EditorGUILayout.EndHorizontal();

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        if (brokenReferences.Count == 0)
        {
            EditorGUILayout.LabelField("No broken script references found on the selected object(s) or their children.");
            EditorGUILayout.HelpBox("Select one or more GameObjects in the Hierarchy. The tool will scan them and all their children for 'Missing Script' components.", MessageType.Info);
        }
        else
        {
            EditorGUILayout.LabelField($"Found {brokenReferences.Count} broken reference(s):", EditorStyles.boldLabel);

            foreach (var reference in brokenReferences)
            {
                DrawBrokenReferenceUI(reference);
            }
        }

        EditorGUILayout.EndScrollView();

        if (_referenceToFix != null)
        {
            FixReferenceInFile(_referenceToFix);
            _referenceToFix = null;
            OnSelectionChanged();
        }
    }

    private void DrawBrokenReferenceUI(BrokenReference reference)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.ObjectField("GameObject", reference.Owner, typeof(GameObject), true);
        EditorGUILayout.BeginHorizontal();
        {
            EditorGUILayout.LabelField("Broken Script GUID:", GUILayout.Width(150));
            EditorGUILayout.SelectableLabel(reference.BrokenGuid, GUILayout.Height(20));
        }
        EditorGUILayout.EndHorizontal();

        if (reference.WasOdinSerialized)
        {
            EditorGUILayout.HelpBox("Odin Serializer fields detected. Original script was likely a 'SerializedMonoBehaviour'.", MessageType.Info);
        }

        EditorGUILayout.LabelField("Suggested Scripts (based on serialized fields):", EditorStyles.boldLabel);
        if (reference.Candidates.Count == 0)
        {
            EditorGUILayout.LabelField("No potential script matches found.");
        }
        else
        {
            int suggestionsToShow = Mathf.Min(reference.Candidates.Count, 20);
            for (int i = 0; i < suggestionsToShow; i++)
            {
                var candidate = reference.Candidates[i];
                // Unique key for each foldout
                string foldoutKey = $"{reference.ComponentFileID}_{candidate.Script.GetInstanceID()}";

                if (!candidateFoldouts.ContainsKey(foldoutKey))
                {
                    candidateFoldouts[foldoutKey] = false;
                }

                EditorGUILayout.BeginVertical(EditorStyles.inspectorDefaultMargins);

                EditorGUILayout.BeginHorizontal();
                string odinLabel = candidate.IsOdinScript ? " (Odin)" : "";
                string label = $"{candidate.MatchCount}/{reference.SerializedFieldNames.Count} matched fields{odinLabel}";

                candidateFoldouts[foldoutKey] = EditorGUILayout.Foldout(candidateFoldouts[foldoutKey], label, true);

                EditorGUILayout.ObjectField(candidate.Script, typeof(MonoScript), false);
                if (GUILayout.Button("Select", GUILayout.Width(60)))
                {
                    reference.NewScript = candidate.Script;
                }
                EditorGUILayout.EndHorizontal();

                if (candidateFoldouts[foldoutKey])
                {
                    EditorGUI.indentLevel++;

                    EditorGUILayout.BeginHorizontal();

                    GUIStyle styleParsedField = GUI.skin.GetStyle("ObjectFieldThumb");

                    EditorGUILayout.BeginVertical();
                    if (candidate.MatchedFields.Any())
                    {
                        EditorGUILayout.LabelField("Matched Fields:", EditorStyles.boldLabel);
                        foreach (var fieldName in candidate.MatchedFields)
                        {
                            EditorGUILayout.LabelField(new GUIContent(
                                $"{fieldName}",
                                EditorGUIUtility.IconContent("Valid").image,
                                "This field exists in both the serialized data and the candidate script."),
                                styleParsedField);
                        }
                    }
                    EditorGUILayout.EndVertical();

                    EditorGUILayout.BeginVertical();

                    if (candidate.UnmatchedFields.Any())
                    {
                        EditorGUILayout.LabelField("Unmatched Fields (in data):", EditorStyles.boldLabel);
                        foreach (var fieldName in candidate.UnmatchedFields)
                        {
                            EditorGUILayout.LabelField(new GUIContent(
                                $"{fieldName}",
                                EditorGUIUtility.IconContent("Error").image,
                                "This field exists in the serialized data but not in the candidate script. Its value will be lost if you fix with this script."),
                                styleParsedField);
                        }
                    }

                    EditorGUILayout.EndVertical();

                    EditorGUILayout.EndHorizontal();

                    EditorGUI.indentLevel--;
                    EditorGUILayout.Space();
                }

                EditorGUILayout.EndVertical();
            }
        }

        EditorGUILayout.Space();

        // Manual assignment field in the case if the suggestions are not satisfying
        reference.NewScript = (MonoScript)EditorGUILayout.ObjectField("Assign Correct Script", reference.NewScript, typeof(MonoScript), false);

        GUI.enabled = reference.NewScript != null;
        if (GUILayout.Button("Fix This Reference"))
        {
            if (EditorUtility.DisplayDialog("Confirm File Modification",
                $"This will modify the file '{Path.GetFileName(reference.FilePath)}' to fix the component on GameObject '{reference.Owner.name}'.\n\n" +
                "Please ensure you have a backup or are using version control. Are you sure?", "Yes, Fix It", "Cancel"))
            {
                _referenceToFix = reference;
            }
        }
        GUI.enabled = true;

        EditorGUILayout.Space();

        var foldoutState = EditorPrefs.GetBool($"MissingScriptResolver_Foldout_{reference.ComponentFileID}", false);
        foldoutState = EditorGUILayout.Foldout(foldoutState, "Serialized Data Preview");
        if (foldoutState)
        {
            EditorGUILayout.SelectableLabel(reference.SerializedDataPreview, EditorStyles.textArea, GUILayout.Height(100), GUILayout.ExpandHeight(true));
        }
        EditorPrefs.SetBool($"MissingScriptResolver_Foldout_{reference.ComponentFileID}", foldoutState);


        EditorGUILayout.EndVertical();
        EditorGUILayout.Space();
    }

    private void FixReferenceInFile(BrokenReference reference)
    {
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(reference.NewScript, out string newGuid, out long newFileID))
        {
            Debug.LogError("Could not get GUID and FileID for the new script. Aborting.", reference.NewScript);
            return;
        }

        string[] allLines = File.ReadAllLines(reference.FilePath);
        bool foundAndFixed = false;

        // Find the start of the specific MonoBehaviour component block using its File ID
        for (int i = 0; i < allLines.Length; i++)
        {
            // A component block starts with "--- !u!114 &<FileID>"
            if (allLines[i].Contains($"--- !u!114 &{reference.ComponentFileID}"))
            {
                // Now search within this block for the m_Script line
                for (int j = i + 1; j < allLines.Length; j++)
                {
                    // Stop if we've hit the next component block
                    if (allLines[j].StartsWith("--- !"))
                    {
                        break;
                    }

                    // Find the line with the broken script reference
                    if (allLines[j].Trim().StartsWith("m_Script:") && allLines[j].Contains(reference.BrokenGuid))
                    {
                        // Capture the indentation of the original line
                        Match indentMatch = Regex.Match(allLines[j], @"^(\s*)");
                        string indentation = indentMatch.Success ? indentMatch.Groups[1].Value : "  "; // Default to two spaces

                        // Construct the new, corrected line with the original indentation
                        string replacementLine = $"{indentation}m_Script: {{fileID: {newFileID}, guid: {newGuid}, type: 3}}";

                        // Replace the line in our array
                        allLines[j] = replacementLine;
                        foundAndFixed = true;
                        break; // Exit the inner loop once the script line is fixed
                    }
                }

                if (foundAndFixed)
                {
                    break; // Exit the outer loop once we've found and fixed our target component
                }
            }
        }

        if (foundAndFixed)
        {
            File.WriteAllLines(reference.FilePath, allLines);
            Debug.Log($"Successfully fixed script reference on component {reference.ComponentFileID} in {Path.GetFileName(reference.FilePath)}.", reference.Owner);
            AssetDatabase.Refresh();
        }
        else
        {
            Debug.LogError($"Failed to find the component with FileID {reference.ComponentFileID} and GUID {reference.BrokenGuid} in {Path.GetFileName(reference.FilePath)}. The file might have been modified externally. Please check the file manually.", reference.Owner);
        }
    }

    private static void BuildScriptCache()
    {
        Type odinType = AppDomain.CurrentDomain.GetAssemblies()
                                 .SelectMany(assembly => assembly.GetTypes())
                                 .FirstOrDefault(t => t.FullName == "Sirenix.OdinInspector.SerializedMonoBehaviour");

        try
        {
            scriptFieldCache = new Dictionary<MonoScript, ScriptCacheInfo>();

            // --- STAGE 1: Process scripts from AssetDatabase (your .cs files) ---
            string[] scriptGuids = AssetDatabase.FindAssets("t:MonoScript");
            EditorUtility.DisplayProgressBar("Building Script Cache", "Processing scripts in Assets...", 0f);

            for (int i = 0; i < scriptGuids.Length; i++)
            {
                string guid = scriptGuids[i];
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);

                if (i % 20 == 0) // Progress bar
                {
                    EditorUtility.DisplayProgressBar("Building Script Cache", $"Processing: {Path.GetFileName(path)}", (float)i / scriptGuids.Length * 0.5f);
                }

                if (script != null)
                {
                    AddScriptToCache(script, odinType);
                }
            }

            // --- STAGE 2: Process scripts from all loaded assemblies (catches DLLs) ---
            var allLoadedScripts = Resources.FindObjectsOfTypeAll<MonoScript>();
            EditorUtility.DisplayProgressBar("Building Script Cache", "Processing scripts from loaded assemblies...", 0.5f);

            for (int i = 0; i < allLoadedScripts.Length; i++)
            {
                var script = allLoadedScripts[i];

                if (i % 50 == 0) // Progress bar
                {
                    EditorUtility.DisplayProgressBar("Building Script Cache", $"Processing: {script.name}", 0.5f + (float)i / allLoadedScripts.Length * 0.5f);
                }

                if (script != null && !scriptFieldCache.ContainsKey(script))
                {
                    AddScriptToCache(script, odinType);
                }
            }
            isCacheBuilt = true;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    private static List<string> GetAllSerializableFields(Type startingType)
    {
        var fieldNames = new HashSet<string>();
        Type currentType = startingType;

        // Walk up the inheritance chain until we hit MonoBehaviour or a non-Unity base class.
        while (currentType != null && currentType != typeof(MonoBehaviour) && currentType.IsSubclassOf(typeof(Component)))
        {
            var fields = currentType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var field in fields)
            {
                if ((field.IsPublic || field.GetCustomAttributes<SerializeField>().Any()) &&
                    !field.GetCustomAttributes<NonSerializedAttribute>().Any())
                {
                    fieldNames.Add(field.Name);
                }
            }
            currentType = currentType.BaseType;
        }
        return fieldNames.ToList();
    }


    private static ulong GetFileID(UnityEngine.Object target)
    {
        if (target == null) return 0;
        PropertyInfo inspectorModeInfo = typeof(SerializedObject).GetProperty("inspectorMode", BindingFlags.NonPublic | BindingFlags.Instance);
        SerializedObject serializedObject = new SerializedObject(target);
        inspectorModeInfo.SetValue(serializedObject, InspectorMode.Debug, null);
        SerializedProperty localIdProp = serializedObject.FindProperty("m_LocalIdentfierInFile");
        return (ulong)localIdProp.longValue;
    }

    private static string GetFilePathForGameObject(GameObject go)
    {
        // Check if it's a prefab instance being edited in the Prefab Stage
        var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
        if (prefabStage != null && prefabStage.IsPartOfPrefabContents(go))
        {
            return prefabStage.assetPath;
        }

        // Check if it's an object in a scene
        if (go.scene.IsValid() && !string.IsNullOrEmpty(go.scene.path))
        {
            return go.scene.path;
        }

        // Check if it's a prefab asset on disk (not an instance in a scene)
        if (PrefabUtility.IsPartOfPrefabAsset(go))
        {
            return AssetDatabase.GetAssetPath(go.transform.root.gameObject);
        }

        return null;
    }

    private static void AddScriptToCache(MonoScript script, Type odinType)
    {
        if (script == null) return;

        // We only care about concrete MonoBehaviour classes that can actually be attached to a GameObject.
        Type scriptType = script.GetClass();
        if (scriptType != null && scriptType.IsSubclassOf(typeof(MonoBehaviour)) && !scriptType.IsAbstract)
        {
            var fieldNames = GetAllSerializableFields(scriptType);

            var cacheInfo = new ScriptCacheInfo
            {
                FieldNames = fieldNames,
                IsOdinScript = odinType != null && scriptType.IsSubclassOf(odinType)
            };
            scriptFieldCache[script] = cacheInfo;
        }
    }
}