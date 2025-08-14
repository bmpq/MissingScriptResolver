using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;

public class MissingScriptResolver : EditorWindow
{
    private class BrokenReference
    {
        public GameObject Owner;
        public string FilePath;
        public ulong ComponentFileID;
        public string BrokenGuid;
        public string SerializedDataPreview;
        public MonoScript NewScript;
    }

    private List<BrokenReference> brokenReferences = new List<BrokenReference>();
    private Vector2 scrollPosition;

    [MenuItem("Tools/Missing Script Resolver")]
    public static void ShowWindow()
    {
        GetWindow<MissingScriptResolver>("Missing Script Resolver");
    }

    private void OnEnable()
    {
        Selection.selectionChanged += OnSelectionChanged;
        OnSelectionChanged();
    }

    private void OnDisable()
    {
        Selection.selectionChanged -= OnSelectionChanged;
    }

    private void OnSelectionChanged()
    {
        FindBrokenReferencesInSelection();
        Repaint();
    }

    private void OnGUI()
    {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        if (brokenReferences.Count == 0)
        {
            EditorGUILayout.LabelField("No broken script references found on the selected object.");
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

        EditorGUILayout.LabelField("Serialized Data Preview:", EditorStyles.boldLabel);
        EditorGUILayout.SelectableLabel(reference.SerializedDataPreview, EditorStyles.textArea, GUILayout.Height(100));

        reference.NewScript = (MonoScript)EditorGUILayout.ObjectField("Assign Correct Script", reference.NewScript, typeof(MonoScript), false);

        GUI.enabled = reference.NewScript != null;
        if (GUILayout.Button("Fix This Reference"))
        {
            if (EditorUtility.DisplayDialog("Confirm File Modification",
                $"This will directly modify the file:\n{Path.GetFileName(reference.FilePath)}\n\n" +
                "Please ensure you have a backup or are using version control. Are you sure?", "Yes, Fix it", "Cancel"))
            {
                FixReferenceInFile(reference);
                OnSelectionChanged();
            }
        }
        GUI.enabled = true;

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space();
    }

    private static ulong GetFileID(Object target)
    {
        if (target == null) return 0;
        GlobalObjectId goid = GlobalObjectId.GetGlobalObjectIdSlow(target);
        return goid.targetObjectId;
    }

    private void FindBrokenReferencesInSelection()
    {
        brokenReferences.Clear();
        var go = Selection.activeGameObject;
        if (go == null) return;

        string filePath = null;
        var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
        if (prefabStage != null && prefabStage.IsPartOfPrefabContents(go))
        {
            filePath = prefabStage.assetPath;
        }
        else if (!string.IsNullOrEmpty(go.scene.path))
        {
            filePath = go.scene.path;
        }
        else
        {
            return;
        }

        if (!File.Exists(filePath)) return;

        ulong targetGoFileID = GetFileID(go);
        if (targetGoFileID == 0) return;

        string[] allLines = File.ReadAllLines(filePath);
        for (int i = 0; i < allLines.Length; i++)
        {
            if (allLines[i].StartsWith("--- !u!114 &"))
            {
                ulong componentFileID = ulong.Parse(Regex.Match(allLines[i], @"&(-?\d+)").Groups[1].Value);

                ulong gameObjectFileID = 0;
                string scriptGuid = null;
                int dataStartIndex = -1;

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

                if (gameObjectFileID == targetGoFileID && !string.IsNullOrEmpty(scriptGuid))
                {
                    if (string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(scriptGuid)))
                    {
                        StringBuilder dataPreview = new StringBuilder();
                        for (int k = dataStartIndex; k < allLines.Length && !allLines[k].StartsWith("--- !"); k++)
                        {
                            if (allLines[k].Trim().Length > 0 && allLines[k].StartsWith("  "))
                            {
                                dataPreview.AppendLine(allLines[k].Trim());
                            }
                        }

                        brokenReferences.Add(new BrokenReference
                        {
                            Owner = go,
                            FilePath = filePath,
                            ComponentFileID = componentFileID,
                            BrokenGuid = scriptGuid,
                            SerializedDataPreview = dataPreview.ToString()
                        });
                    }
                }
            }
        }
    }

    private void FixReferenceInFile(BrokenReference reference)
    {
        if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(reference.NewScript, out string newGuid, out long newFileID))
        {
            Debug.LogError("Could not get GUID and FileID for the new script. Aborting.", reference.NewScript);
            return;
        }

        string fileContent = File.ReadAllText(reference.FilePath);

        string regexPattern = $@"m_Script: {{fileID: -?\d+, guid: {reference.BrokenGuid}, type: 3}}";
        string replacementLine = $"m_Script: {{fileID: {newFileID}, guid: {newGuid}, type: 3}}";

        string componentHeader = $"--- !u!114 &{(long)reference.ComponentFileID}";
        int componentIndex = fileContent.IndexOf(componentHeader);

        if (componentIndex == -1)
        {
            Debug.LogError($"Could not find component with fileID {(long)reference.ComponentFileID} in file {reference.FilePath}. Aborting.");
            return;
        }

        int nextComponentIndex = fileContent.IndexOf("--- !", componentIndex + 1);
        if (nextComponentIndex == -1)
        {
            nextComponentIndex = fileContent.Length;
        }

        string componentBlock = fileContent.Substring(componentIndex, nextComponentIndex - componentIndex);
        string newComponentBlock = Regex.Replace(componentBlock, regexPattern, replacementLine, RegexOptions.Singleline);

        if (componentBlock.Equals(newComponentBlock))
        {
            Debug.LogError($"Failed to find and replace the broken script line within the component block for {reference.Owner.name}. The script line might be malformed. Please check the file manually.", reference.Owner);
            return;
        }

        fileContent = fileContent.Substring(0, componentIndex) + newComponentBlock + fileContent.Substring(nextComponentIndex);

        File.WriteAllText(reference.FilePath, fileContent);

        Debug.Log($"Successfully replaced script reference in {Path.GetFileName(reference.FilePath)} for component on {reference.Owner.name}", reference.Owner);

        AssetDatabase.Refresh();
    }
}