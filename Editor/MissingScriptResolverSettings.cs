using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

public class MissingScriptResolverSettings : ScriptableObject
{
    public int searchLimit = 3;
    public bool skipWarnings = false;

    private const string k_Path = "ProjectSettings/MissingScriptResolverSettings.asset";

    internal static MissingScriptResolverSettings GetOrCreateSettings()
    {
        MissingScriptResolverSettings settings = null;
        var settingsArray = InternalEditorUtility.LoadSerializedFileAndForget(k_Path);
        if (settingsArray.Length > 0 && settingsArray[0] is MissingScriptResolverSettings loadedSettings)
        {
            settings = loadedSettings;
        }

        if (settings == null)
        {
            settings = CreateInstance<MissingScriptResolverSettings>();
        }

        return settings;
    }

    internal static void Save(MissingScriptResolverSettings settings)
    {
        InternalEditorUtility.SaveToSerializedFileAndForget(new[] { settings }, k_Path, true);
    }
}

static class MissingScriptResolverSettingsProvider
{
    private static SerializedObject m_SerializedSettings;

    [SettingsProvider]
    public static SettingsProvider CreateMyToolProvider()
    {
        var provider = new SettingsProvider("Project/MissingScriptResolver", SettingsScope.Project)
        {
            label = "MissingScriptResolver",
            guiHandler = (searchContext) =>
            {
                var settings = MissingScriptResolverSettings.GetOrCreateSettings();

                if (m_SerializedSettings == null || m_SerializedSettings.targetObject != settings)
                {
                    m_SerializedSettings = new SerializedObject(settings);
                }

                EditorGUI.BeginChangeCheck();

                EditorGUILayout.PropertyField(m_SerializedSettings.FindProperty("searchLimit"));
                EditorGUILayout.PropertyField(m_SerializedSettings.FindProperty("skipWarnings"));

                if (EditorGUI.EndChangeCheck())
                {
                    m_SerializedSettings.ApplyModifiedProperties();
                    MissingScriptResolverSettings.Save(settings);
                }
            },
            keywords = new System.Collections.Generic.HashSet<string>(new[] { "script", "reference", "missing" })
        };

        return provider;
    }
}
