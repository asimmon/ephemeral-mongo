namespace EphemeralMongo;

internal interface IFileSystem
{
    void CreateDirectory(string path);

    void DeleteDirectory(string path);

    void DeleteFile(string path);

    void MakeFileExecutable(string path);
}