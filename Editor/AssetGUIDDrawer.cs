using UnityEngine;
using UnityEditor;

/// <summary>
/// Display the GUID and localIDs for any selected project asset in the inspector
/// </summary>
[InitializeOnLoad]
public static class AssetGUIDDrawer
{
    static AssetGUIDDrawer()
    {
        Editor.finishedDefaultHeaderGUI += OnDrawHeaderGUI;
    }

    private static bool expandLocalIDs = false;

    private static void OnDrawHeaderGUI(Editor editor)
    {
        UnityEngine.Object targetObject = editor.target;

        string assetPath = AssetDatabase.GetAssetPath(targetObject);

        if (!string.IsNullOrEmpty(assetPath))
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);

            bool sceneAsset = AssetDatabase.GetMainAssetTypeAtPath(assetPath) == typeof(SceneAsset);

            EditorGUILayout.BeginHorizontal();
            {
                EditorGUILayout.LabelField("Asset GUID", GUILayout.Width(90));
                EditorGUILayout.SelectableLabel(guid, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
            }
            EditorGUILayout.EndHorizontal();

            if (!sceneAsset)
            {
                var allObjectsInAsset = AssetDatabase.LoadAllAssetsAtPath(assetPath);

                if (allObjectsInAsset.Length > 0)
                {
                    EditorGUI.indentLevel++;
                    expandLocalIDs = EditorGUILayout.Foldout(expandLocalIDs, "Local IDs", true);

                    if (expandLocalIDs)
                    {
                        for (int i = 0; i < allObjectsInAsset.Length; i++)
                        {
                            if (allObjectsInAsset[i] == null)
                                continue;

                            if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(allObjectsInAsset[i], out string guid2, out long localid))
                            {
                                EditorGUILayout.BeginHorizontal();
                                EditorGUILayout.LabelField($"{allObjectsInAsset[i].name} ({allObjectsInAsset[i].GetType().Name})");
                                EditorGUILayout.SelectableLabel(localid.ToString(), EditorStyles.textField, GUILayout.MaxWidth(200), GUILayout.Height(EditorGUIUtility.singleLineHeight));
                                EditorGUILayout.EndHorizontal();
                            }
                        }
                    }
                    EditorGUI.indentLevel--;
                }
            }
        }
    }
}
