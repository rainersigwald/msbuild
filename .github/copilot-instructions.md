This repo contains the code for the MSBuild build engine, including its public C# API, its internal implementation of the MSBuild programming language, and core targets and tasks used for builds for .NET and Visual Studio.

Performance is very important--minimize allocations, avoid LINQ, and use the most efficient algorithms possible. The code should be easy to read and understand, but performance is the top priority.

The code is written in C# and should follow the .NET coding conventions. Use the latest C# features where appropriate, including C# 13 features and especially collection expressions--prefer `[]` to `new Type[]`.

You should generally match the style of surrounding code when making edits, but if making a substantial change, you can modernize more aggressively.

Generate tests for new codepaths, and add tests for any bugs you fix. Use the existing test framework, which is xUnit with Shouldly assertions. Use Shouldly assertions for all assertions in modified code, even if the file is predominantly using xUnit assertions.
