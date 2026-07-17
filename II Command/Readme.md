# Infirmary Integrated: Command

Instructor dashboard for multi-room simulation labs. Hosts N concurrent scenario sessions from one window — each room is a card showing live vitals (left half) and the current step's branch triggers (right half) — and broadcasts every session to the mirror relay server so student-room machines can receive them.

This is an institutional-deployment addition to the fork; it is not part of upstream Infirmary Integrated.

## Build

Requires the .NET 10 SDK. From the repository root:

```
dotnet build "II Command\II Command.csproj"
```

Or open `Infirmary Integrated.sln` in Visual Studio and build/run the **II Command** project.

## Instructor workflow (control PC)

1. Set the mirror server address in the toolbar (e.g. your self-hosted Pi: `http://192.168.x.x/`).
2. **+ Add Room** — pick one or more `.ii` scenario files; each becomes a room card. Room names are editable; accession keys and access passwords are auto-generated per room.
3. **Broadcast All** (or per-card **Broadcast**) — each card shows `● LIVE` with its accession + password once the server accepts it.
4. Run the class from the cards: branch buttons advance to that progression; Next takes the default branch; Prev/⏸/⏵ control step flow and timers.
5. **Save Layout…** stores the whole setup (server, rooms, scenarios, credentials) as an `.iicmd` file; **Load Layout…** restores it next class.

## Student-room machines

Use the II:SIM command-line options added to this fork to launch each room's machine straight into its session, fullscreen on the patient monitor:

```
"Infirmary Integrated.exe" --receive --accession ROOM01KEY --password SECRET --server http://192.168.x.x/ --monitor --fullscreen
```

Put that in a desktop shortcut (or startup script) per room, using the accession/password shown on that room's card or saved in the `.iicmd` layout.

## Notes

- The dashboard renders numerics only; receiving II:SIM clients draw their own waveforms from the broadcast physiology.
- Clients poll the relay server every ~5 seconds, so step changes appear in rooms within a few seconds.
- Concurrent session count is limited only by server/bandwidth; 12+ cards on a 4K monitor is the design target.
