# Troubleshooting incremental builds

A nice thing about large builds is that most of the time you're only changing a small part of the build. That lets MSBuild avoid doing work: when inputs haven't changed, there's no need to recompute outputs!

In the best case, just running a build twice in a row (without changing the inputs to the build) shouldn't require doing any work at all.

Unfortunately, sometimes unnecessary rebuilds happen. This document is intended to help troubleshoot those situations. It's a work in progress, and improvement pull requests are welcome.

## Overview: How MSBuild incrementality works

MSBuild incrementality is primarily controlled at the target level. Any target that has `Inputs` and `Outputs` attributes defined may be skipped because it is up to date. Generally, a target is up to date if all of its `Outputs` are newer than all of its `Inputs`. However, if there are exactly as many `Outputs` as `Inputs`, the MSBuild engine assumes that each output corresponds to the input that has the same list order. In that case, the target may be *partially* executed--only for the inputs that are newer than their corresponding outputs.   

## Missing target inputs and outputs

## Tasks that handle incrementality

## Following the cascade

## Builds from Visual Studio

All of the above applies equally to builds started from the command line and those started from the Visual Studio UI. But Visual Studio has an additional layer of incrementality called the "fast up-to-date check." This attempts to understand just enough about the project file to predict project-level inputs and outputs. If the heuristics in the fast up-to-date check indicate that no output is stale, Visual Studio won't even ask MSBuild to build.

If all goes well, you'll see output in the Build window like

    ========== Build: 0 succeeded, 0 failed, 1 up-to-date, 0 skipped ==========
	
Anything that's not listed in "up-to-date" will invoke MSBuild. If you set `MSBuild project build output verbosity` in Options, Projects and Solutions, Build and Run to `Diagnostic` level, the project system will emit logging to explain why the fast up-to-date check failed, for instance

    1>Project 'VBConsole' is not up to date. Input file 'c:\work\simple_samples\vbconsole\vbconsole\module1.vb' is modified after output file 'c:\work\simple_samples\VBConsole\VBConsole\bin\Debug\VBConsole.pdb'.
