# Fire3D Unity

Unity client project for Fire3D.

## Prerequisites

- Unity `6000.6.3f1` (Unity 6.6)
- Android Build Support, including Android SDK & NDK Tools and OpenJDK, to create Android builds
- Git LFS for binary assets

## Layout

- `Assets/_Fire3D/Runtime` — runtime C# assemblies
- `Assets/_Fire3D/Editor` — editor-only tools
- `Assets/_Fire3D/Scenes` — project scenes
- `Assets/_Fire3D/Art` — project-owned art assets

Do not commit Unity-generated directories such as `Library`, `Temp`, `Logs`, or `UserSettings`.
The Unity app calls the backend API; API credentials and backend secrets must never be placed in this repository.
