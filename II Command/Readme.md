# Infirmary Integrated: Command

Instructor dashboard for multi-room simulation labs. Hosts N concurrent scenario sessions from one window — each room is a card showing live vitals (left half) and the current step's branch triggers (right half) — and broadcasts every session to the mirror relay server so student-room machines can receive them.

This is an institutional-deployment addition to the fork; it is not part of upstream Infirmary Integrated.

## Build

Requires the .NET 10 SDK. From the repository root:

```
dotnet build "II Command\II Command.csproj"
```

Or open `Infirmary Integrated.sln` in Visual Studio and build/run the **II Command** project.

## The model: rooms are permanent, scenarios are per class

A **room** is a physical sim room: a name plus fixed credentials (accession, access password,
edit password). You set those once, save them in the layout, and bake them into that room
PC's launcher -- after that the room PC connects by itself at startup and never needs to
know what you loaded today. A **scenario** is dropped into a room per class and can be
swapped at any time without touching the room's identity or its broadcast.

While a room is LIVE with no scenario loaded it broadcasts a resting default patient, so
room PCs that boot early still connect and show something sensible until the scenario arrives.

## One-time lab setup

1. **+ Add Room** once per physical room. Each card gets a placeholder accession; open
   **Room…** and set a permanent name, accession (letters/digits, e.g. `SIMROOM01`), access
   password, and edit password. The edit password protects the accession from being overwritten
   by anyone else using the same relay -- keep it set.
2. Set the mirror server address in the toolbar.
3. **Save Layout…** to an `.iicmd` file. This is the lab's identity; back it up.
4. **Export Room Launchers…** -- pick the II:SIM executable path as installed on the room PCs and
   an output folder. Copy each `<Room name>.bat` to its room PC's Startup folder. Re-export only
   if credentials or the server change.

## Every class

1. **Load Layout…** -- the rooms come back with their credentials.
2. **Broadcast All**. Rooms go `● LIVE`; room PCs connect (or are already connected).
3. Per card, **Scenario…** to load that room's `.ii` for today. Loading into a LIVE room pushes
   it to the room PC immediately.
4. Run the class from the cards: branch buttons advance to that progression; **Next** takes the
   default branch (disabled when the step has none); Prev/⏸/⏵ control step flow and timers;
   **Edit Vitals…** overrides rhythm, HR, BP, SpO2, RR, EtCO2 and temperature for the current
   step, applied to the room within a poll cycle.

**Compact** (toolbar checkbox) shrinks the cards. Comfortable density fits 12 rooms on a 4K
display; Compact fits 12 on 1080p without scrolling (4 columns x 3 rows). All six vitals stay
visible at either density. The window opens maximized.

## Student-room machines

Use the II:SIM command-line options added to this fork to launch each room's machine straight into its session, fullscreen on the patient monitor:

```
"C:\Program Files\Infirmary Integrated\infirmary-integrated\infirmary-integrated.exe" --receive --accession ROOM01KEY --password SECRET --server http://192.168.x.x/ --monitor --fullscreen
```

**Export Room Launchers…** writes these for you: pick the II:SIM executable as installed on the
room machines (remembered in the layout thereafter) and an output folder, and II Command emits one
`<Room name>.bat` per card with that room's accession, password and server baked in. Copy each to
its room machine's desktop or Startup folder. Re-export after changing rooms or credentials.

## Notes

- The dashboard renders numerics only; receiving II:SIM clients draw their own waveforms from the broadcast physiology.
- Clients poll the relay server every ~5 seconds, so step changes appear in rooms within a few seconds.
- Concurrent session count is limited only by server/bandwidth; 12+ cards on a 4K monitor is the design target.
