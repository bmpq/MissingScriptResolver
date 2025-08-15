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
        OnSelectionChanged();
    }

    private void OnDisable()
    {
        Selection.selectionChanged -= OnSelectionChanged;
    }

    private void OnSelectionChanged()
    {
        FindBrokenReferencesInSelection();
        if (brokenReferences.Count > 0)
        {
            FindAndRankCandidatesForAll();
        }
        Repaint();
    }

    private void FindBrokenReferencesInSelection()
    {
        brokenReferences.Clear();
        candidateFoldouts.Clear();
        var go = Selection.activeGameObject;
        if (go == null) return;

        string filePath = GetFilePathForGameObject(go);
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        ulong targetGoFileID = GetFileID(go);
        if (targetGoFileID == 0) return;

        string[] allLines = File.ReadAllLines(filePath);
        for (int i = 0; i < allLines.Length; i++)
        {
            // Find a MonoBehaviour component block
            if (allLines[i].StartsWith("--- !u!114 &"))
            {
                ulong componentFileID = ulong.Parse(Regex.Match(allLines[i], @"&(-?\d+)").Groups[1].Value);

                ulong gameObjectFileID = 0;
                string scriptGuid = null;
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
                        var match = Regex.Match(allLines[j], @"guid: ([a-f0-9]{32})");
                        if (match.Success) scriptGuid = match.Groups[1].Value;
                        dataStartIndex = j + 1;
                    }
                }

                // Check if this component belongs to our selected GameObject and its script is missing
                if (gameObjectFileID == targetGoFileID && !string.IsNullOrEmpty(scriptGuid) &&
                    string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(scriptGuid)))
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

                if (matchedFields.Count > 0 || (reference.WasOdinSerialized && scriptInfo.IsOdinScript))
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
            EditorGUILayout.LabelField("No broken script references found on the selected object(s).");
            EditorGUILayout.HelpBox("Select a GameObject in the Hierarchy that has a 'Missing Script' component.", MessageType.Info);
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
        EditorGUILayout.LabelField("Broken Script GUID:", reference.BrokenGuid);

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
            int suggestionsToShow = Mathf.Min(reference.Candidates.Count, 5);
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
                $"This will find and replace ALL occurrences of the broken script GUID in the file:\n{Path.GetFileName(reference.FilePath)}\n\n" +
                "Please ensure you have a backup or are using version control. Are you sure?", "Yes, Fix All", "Cancel"))
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

        string fileContent = File.ReadAllText(reference.FilePath);

        // This pattern will find all occurrences of the m_Script line with the broken GUID.
        // The fileID can be different for different references (e.g., 11500000 or -765806418), so we use a wildcard.
        string regexPattern = $@"m_Script: {{fileID: -?\d+, guid: {reference.BrokenGuid}, type: 3}}";

        // This is the new line that will replace the broken ones.
        string replacementLine = $"m_Script: {{fileID: {newFileID}, guid: {newGuid}, type: 3}}";

        // Perform a global replacement across the entire file content.
        string newFileContent = Regex.Replace(fileContent, regexPattern, replacementLine);

        // Check if any changes were actually made.
        if (fileContent.Equals(newFileContent))
        {
            Debug.LogError($"Failed to find and replace any script lines with GUID {reference.BrokenGuid} in {Path.GetFileName(reference.FilePath)}. The script line might be malformed. Please check the file manually.", reference.Owner);
            return;
        }

        File.WriteAllText(reference.FilePath, newFileContent);
        Debug.Log($"Successfully replaced all script references with GUID {reference.BrokenGuid} in {Path.GetFileName(reference.FilePath)}.", reference.Owner);

        AssetDatabase.Refresh();
    }

    private static void BuildScriptCache()
    {
        Type odinType = AppDomain.CurrentDomain.GetAssemblies()
                                 .SelectMany(assembly => assembly.GetTypes())
                                 .FirstOrDefault(t => t.FullName == "Sirenix.OdinInspector.SerializedMonoBehaviour");

        try
        {
            EditorUtility.DisplayProgressBar("Building Script Cache", "Finding all scripts...", 0.1f);
            scriptFieldCache = new Dictionary<MonoScript, ScriptCacheInfo>();
            string[] scriptGuids = AssetDatabase.FindAssets("t:MonoScript");

            for (int i = 0; i < scriptGuids.Length; i++)
            {
                string guid = scriptGuids[i];
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(path);

                if (i % 20 == 0)
                {
                    EditorUtility.DisplayProgressBar("Building Script Cache", $"Processing: {Path.GetFileName(path)}", (float)i / scriptGuids.Length);
                }

                if (script != null)
                {
                    Type scriptType = script.GetClass();
                    if (scriptType != null && scriptType.IsSubclassOf(typeof(MonoBehaviour)))
                    {
                        var fieldNames = new List<string>();
                        var fields = scriptType.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        foreach (var field in fields)
                        {
                            if (field.IsPublic || field.GetCustomAttribute<SerializeField>() != null)
                            {
                                if (field.GetCustomAttribute<NonSerializedAttribute>() == null)
                                {
                                    fieldNames.Add(field.Name);
                                }
                            }
                        }

                        var cacheInfo = new ScriptCacheInfo
                        {
                            FieldNames = fieldNames,
                            IsOdinScript = odinType != null && scriptType.IsSubclassOf(odinType)
                        };
                        scriptFieldCache[script] = cacheInfo;
                    }
                }
            }
            isCacheBuilt = true;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
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
        if (!string.IsNullOrEmpty(go.scene.path))
        {
            return go.scene.path;
        }

        return null;
    }
}