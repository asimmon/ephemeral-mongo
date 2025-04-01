namespace EphemeralMongo;

internal interface IFileSystem
{
    void CreateDirectory(string path);

    void DeleteDirectory(string path);

    void DeleteFile(string path);

    string[] GetDirectories(string path, string searchPattern, SearchOption searchOption);

    DateTime GetDirectoryCreationTimeUtc(string path);

    void MakeFileExecutable(string path);
}