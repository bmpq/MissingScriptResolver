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
    }

    private class ScriptCandidate
    {
        public MonoScript Script;
        public int MatchCount;
    }

    private List<BrokenReference> brokenReferences = new List<BrokenReference>();
    private Vector2 scrollPosition;

    private static Dictionary<MonoScript, List<string>> scriptFieldCache;
    private static bool isCacheBuilt = false;

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
                                reference.SerializedFieldNames.Add(match.Groups[1].Value);
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
            if (reference.SerializedFieldNames.Count == 0) continue;

            foreach (var cacheEntry in scriptFieldCache)
            {
                var script = cacheEntry.Key;
                var scriptFields = cacheEntry.Value;

                int matchCount = reference.SerializedFieldNames.Count(fieldName => scriptFields.Contains(fieldName));

                if (matchCount > 0)
                {
                    reference.Candidates.Add(new ScriptCandidate
                    {
                        Script = script,
                        MatchCount = matchCount
                    });
                }
            }

            // Sort candidates by the number of matches, descending
            reference.Candidates.Sort((a, b) => b.MatchCount.CompareTo(a.MatchCount));
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
    }
    private void DrawBrokenReferenceUI(BrokenReference reference)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.ObjectField("GameObject", reference.Owner, typeof(GameObject), true);
        EditorGUILayout.LabelField("Broken Script GUID:", reference.BrokenGuid);

        EditorGUILayout.LabelField("Suggested Scripts (based on serialized fields):", EditorStyles.boldLabel);
        if (reference.Candidates.Count == 0)
        {
            EditorGUILayout.LabelField("No potential script matches found.");
        }
        else
        {
            int suggestionsToShow = Mathf.Min(reference.Candidates.Count, 3);
            for (int i = 0; i < suggestionsToShow; i++)
            {
                var candidate = reference.Candidates[i];
                EditorGUILayout.BeginHorizontal();
                string label = $"{candidate.MatchCount}/{reference.SerializedFieldNames.Count} matched fields";
                EditorGUILayout.ObjectField(label, candidate.Script, typeof(MonoScript), false);
                if (GUILayout.Button("Select", GUILayout.Width(60)))
                {
                    reference.NewScript = candidate.Script;
                }
                EditorGUILayout.EndHorizontal();
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
                FixReferenceInFile(reference);
                OnSelectionChanged();
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
        try
        {
            EditorUtility.DisplayProgressBar("Building Script Cache", "Finding all scripts...", 0.1f);
            scriptFieldCache = new Dictionary<MonoScript, List<string>>();
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
                        scriptFieldCache[script] = fieldNames;
                    }
                }
            }
            isCacheBuilt = true;
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
        Debug.Log($"Script field cache built with {scriptFieldCache.Count} MonoBehaviour entries.");
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