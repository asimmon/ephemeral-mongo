<!-- omit from toc -->
# EphemeralMongo

[![ci](https://img.shields.io/github/actions/workflow/status/asimmon/ephemeral-mongo/ci.yml?logo=github&label=ci)](https://github.com/asimmon/ephemeral-mongo/actions/workflows/ci.yml)
[![publish](https://img.shields.io/github/actions/workflow/status/asimmon/ephemeral-mongo/release.yml?logo=github&label=release)](https://github.com/asimmon/ephemeral-mongo/actions/workflows/release.yml)

- [About](#about)
- [Installation](#installation)
- [Getting started](#getting-started)
- [How it works](#how-it-works)
- [Windows Defender Firewall prompt](#windows-defender-firewall-prompt)
- [Optimization tips](#optimization-tips)
- [Changelog](#changelog)

## About

**EphemeralMongo** is a .NET library that provides a simple way to run temporary and disposable MongoDB servers for integration tests and local debugging, without any dependencies or external tools. It is as simple as:

```csharp
using var runner = await MongoRunner.RunAsync();
Console.WriteLine(runner.ConnectionString);
```

Use the connection string to access the MongoDB server. It will be automatically be stopped and cleaned up when the `runner` is disposed. You can pass options to customize the server, such as the data directory, port, and replica set configuration. More details can be found in the [usage section](#usage).

Overall, this library provides:
- Multiple ephemeral and **isolated MongoDB databases** for running tests.l
- A quick way to set up a MongoDB database for a local development environment.
- Access to **MongoDB 6**, **7**, and **8**.
- Access to **MongoDB Community** and **Enterprise editions**.
- Support for Linux, macOS, and Windows.
- Support for **single-node replica sets**, enabling transactions and change streams.
- _mongoexport_ and _mongoimport_ tools for [exporting](https://www.mongodb.com/docs/database-tools/mongoexport/) and [importing](https://docs.mongodb.com/database-tools/mongoimport/) collections.
- Support for .NET Standard 2.0, .NET Standard 2.1, .NET 8.0 and later.

This project follows in the footsteps of [Mongo2Go](https://github.com/Mongo2Go/Mongo2Go) and expands upon its foundation.

## Installation

| Package | Description |
| --- | --- |
| [![nuget](https://img.shields.io/nuget/v/EphemeralMongo.svg?logo=nuget)](https://www.nuget.org/packages/EphemeralMongo/) | Install this package if you use MongoDB C# driver version 3.x. |
| [![nuget](https://img.shields.io/nuget/v/EphemeralMongo.v2.svg?logo=nuget)](https://www.nuget.org/packages/EphemeralMongo.v2/) | Install this package if you use MongoDB C# driver version 2.x. |

## Getting started

Use `MongoRunner.RunAsync()` or `MongoRunner.Run()` methods to create a disposable instance that provides access to a **MongoDB connection string**, **import**, and **export tools**. An optional `MongoRunnerOptions` parameter can be provided to customize the MongoDB server.

```csharp
// All the following properties are OPTIONAL.
var options = new MongoRunnerOptions
{
    // The desired MongoDB version to download and use.
    // Possible values are V6, V7 and V8. Default is V8.
    Version = MongoVersion.V8,

    // The desired MongoDB edition to download and use.
    // Possible values are Community and Enterprise. Default is Community.
    Edition = MongoEdition.Community,

    // If true, the MongoDB server will run as a single-node replica set. Default is false.
    UseSingleNodeReplicaSet = true,

    // Additional arguments to pass to the MongoDB server. Default is null.
    AdditionalArguments = ["--quiet"],

    // The port on which the MongoDB server will listen.
    // Default is null, which means a random available port will be assigned.
    MongoPort = 27017,

    // The directory where the MongoDB server will store its data.
    // Default is null, which means a temporary directory will be created.
    DataDirectory = "/path/to/data/",

    // Provide your own MongoDB binaries in this directory.
    // Default is null, which means the library will download them automatically.
    BinaryDirectory = "/path/to/mongo/bin/",

    // A delegate that receives the MongoDB server's standard output.
    StandardOutputLogger = Console.WriteLine,

    // A delegate that receives the MongoDB server's standard error output.
    StandardErrorLogger = Console.WriteLine,

    // Timeout for the MongoDB server to start. Default is 30 seconds.
    ConnectionTimeout = TimeSpan.FromSeconds(10),

    // Timeout for the replica set to be initialized. Default is 10 seconds.
    ReplicaSetSetupTimeout = TimeSpan.FromSeconds(5),

    // The duration for which temporary data directories will be kept.
    // Ignored when you provide your own data directory. Default is 12 hours.
    DataDirectoryLifetime = TimeSpan.FromDays(1),

    // Override this property to provide your own HTTP transport.
    // Useful when behind a proxy or firewall. Default is a shared reusable instance.
    Transport = new HttpTransport(new HttpClient()),

    // Delay before checking for a new version of the MongoDB server. Default is 1 day.
    NewVersionCheckTimeout = TimeSpan.FromDays(2)
};
```

```csharp
// Disposing the runner will kill the MongoDB process (mongod) and delete the associated data directory
using (await var runner = MongoRunner.RunAsync(options))
{
    var database = new MongoClient(runner.ConnectionString).GetDatabase("default");

    // Do something with the database
    database.CreateCollection("people");

    // Export a collection to a file. Synchronous version is also available.
    await runner.ExportAsync("default", "people", "/path/to/default.json");

    // Import a collection from a file. Synchronous version is also available.
    await runner.ImportAsync("default", "people", "/path/to/default.json");
}
```

## How it works

* At runtime, the MongoDB binaries (`mongod`, `mongoimport`, and `mongoexport`) are downloaded (if not already present) and extracted to your local application data directory.
* `MongoRunner.Run` always starts a new `mongod` process with a random available port,
* The resulting connection string will depend on your options (`UseSingleNodeReplicaSet`),
* By default, a unique temporary data directory is used.

## Windows Defender Firewall prompt

On Windows, you might get a **Windows Defender Firewall prompt**.
This is because EphemeralMongo starts the `mongod.exe` process from your local app data directory, and `mongod.exe` tries to open an available port.

## Optimization tips

Avoid calling `MongoRunner.Run` concurrently, as this will create many `mongod` processes and make your operating system slower.
Instead, try to use a single instance and reuse it - create as many databases as you need, one per test, for example.

Check out [this gist](https://gist.github.com/asimmon/612b2d54f1a0d2b4e1115590d456e0be) for an implementation of a reusable `IMongoRunner`.

## Changelog

### 2.0.0

- **Breaking change**: Support for MongoDB 5.0 and 6.0 has been removed, as their [end-of-life](https://www.mongodb.com/legal/support-policy/lifecycles) has passed.
- **Breaking change**: arm64 is now the default target for macOS. The previous target was x64.
- **Breaking change**: The Linux runtime package now uses Ubuntu 22.04's MongoDB binaries instead of the 18.04 ones. OpenSSL 3.0 is now required.
- **Breaking change**: Updated the MongoDB C# driver to 2.28.0, [which now uses strong-named assemblies](https://www.mongodb.com/community/forums/t/net-driver-2-28-0-released/289745).
- Added support for MongoDB 8.0.
- Introduced data directory management to delete old data directories automatically.
- Use direct connection in replica set connection strings.
- Fixed the spelling issue in `MongoRunnerOptions.StandardOutputLogger`.
