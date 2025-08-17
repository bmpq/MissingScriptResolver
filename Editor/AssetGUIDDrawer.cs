using UnityEngine;
using UnityEditor;

/// <summary>
/// Display the GUID for any selected project asset in the inspector
/// </summary>
[InitializeOnLoad]
public static class AssetGUIDDrawer
{
    static AssetGUIDDrawer()
    {
        Editor.finishedDefaultHeaderGUI += OnDrawHeaderGUI;
    }

    private static void OnDrawHeaderGUI(Editor editor)
    {
        UnityEngine.Object targetObject = editor.target;

        string assetPath = AssetDatabase.GetAssetPath(targetObject);

        if (!string.IsNullOrEmpty(assetPath))
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);

            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.LabelField("Asset GUID", GUILayout.Width(75));
                EditorGUILayout.SelectableLabel(guid, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            EditorGUILayout.EndHorizontal();
        }
    }
}
