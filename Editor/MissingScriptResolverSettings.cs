using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

public class MissingScriptResolverSettings : ScriptableObject
{
    public int searchLimit = 10;

    const string k_Path = "ProjectSettings/MissingScriptResolverSettings.asset";

    public static MissingScriptResolverSettings GetOrCreateSettings()
    {
        var settings = InternalEditorUtility.LoadSerializedFileAndForget(k_Path);
        if (settings.Length > 0 && settings[0] is MissingScriptResolverSettings loaded)
            return loaded;

        var instance = CreateInstance<MissingScriptResolverSettings>();
        Save(instance);
        return instance;
    }

    public static void Save(MissingScriptResolverSettings settings)
    {
        InternalEditorUtility.SaveToSerializedFileAndForget(new[] { settings }, k_Path, true);
    }
}

static class MissingScriptResolverSettingsProvider
{
    [SettingsProvider]
    public static SettingsProvider CreateMyToolProvider()
    {
        var provider = new SettingsProvider("Project/MissingScriptResolver", SettingsScope.Project)
        {
            label = "MissingScriptResolver",
            guiHandler = (searchContext) =>
            {
                var settings = MissingScriptResolverSettings.GetOrCreateSettings();
                var so = new SerializedObject(settings);
                EditorGUILayout.PropertyField(so.FindProperty("searchLimit"));
                so.ApplyModifiedProperties();
            },
            keywords = new System.Collections.Generic.HashSet<string>(new[] { "script", "reference", "missing" })
        };

        return provider;
    }
}
