# Debugging MSBuild

MSBuild can be debugged using any .NET debugger, but some setup is required to get a smooth experience.

```
devenv.exe /debugexe msbuild.exe {rest of command line}
```

https://github.com/dotnet/coreclr/blob/master/Documentation/project-docs/clr-configuration-knobs.md

## Child processes

When MSBuild is invoked with `/m` or its API is used

## Debugging task execution

The MSBuild engine runs tasks by

1. Constructing an object of the class type,
1. Setting public fields corresponding to the XML attributes used in the task invocation, and
1. Calling `ITask.Execute()` on the object.

You can break into the execution of any task by setting a breakpoint at `{TaskClassName}.Execute`.

### If you control the task

### Third-party tasks

