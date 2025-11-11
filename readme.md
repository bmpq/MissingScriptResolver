## How it works
Unity stores scene and prefab data in human-readable YAML. Even when a script is "missing," the data it serialized is often still present in the file. This tool leverages that fact.
1. File Parsing: It reads the raw text of the `.unity` or `.prefab` file.
2. Broken Reference Detection: It looks for MonoBehaviour components where the script guid doesn't point to a valid asset in the project.
3. Data Extraction: For a broken component, it extracts the names of all the serialized fields from the YAML data.
4. Script Caching: On the first run, it builds a cache of all MonoScript assets in your project, analyzing each one to get a list of its serializable fields (using reflection). This includes public fields and private fields with the `[SerializeField]` attribute.
5. Matching & Ranking: It compares the list of fields from the broken component against the fields of every script in its cache. The more names that match, the higher the script's score.
6. File Modification: When you click "Fix", it finds the exact `m_Script` line for the broken component in the file and replaces the old, invalid guid and fileID with the new ones from your chosen script.


## Installation
1. Open UPM in Unity: **Window > Package Management > Package Manager**
2. Click **"+"** button at the top left
3. Select **"Add package from git URL..."** and paste following URL:
```
https://github.com/bmpq/MissingScriptResolver.git
```
