# Godot acceptance assembly

Production `APP-AGAS-CS.csproj` excludes every source under `tests/`. The
acceptance composition has its own assembly name and output directory, so
building it never replaces `.godot/.../APP-AGAS-CS.dll`.

Run a scene through the disposable-snapshot runner:

```bash
tests/Godot/run-acceptance.sh ProjectPickerAcceptance.tscn
```

The runner copies the current workspace without `.git`, `.godot`, `bin`, or
`obj`, changes `project/assembly_name` only inside that disposable snapshot,
builds the isolated acceptance assembly, stages it under the snapshot's Godot
output, and runs the requested scene. The real workspace's `project.godot` and
production DLL are never changed. Set `GODOT_MONO_BIN` when Godot Mono is not
installed at `/Applications/Godot_mono.app`; set
`AGAS_KEEP_ACCEPTANCE_SNAPSHOT=1` to keep a failed snapshot for inspection.
