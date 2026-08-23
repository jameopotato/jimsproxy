JimsProxy (win-x64)
===================

A protocol translation proxy that lets WoW Classic 1.14.2 clients connect to
vanilla 1.12.1 servers. Primary target: Kronos.

This is the standalone build, for running the proxy yourself. If you'd rather
have setup, updates, addons and game launch handled for you, use the launcher
instead: https://jimothy.cc/install

Version info is in manifest.json.
Full documentation: https://github.com/jameopotato/jimsproxy


WHAT'S IN HERE
--------------
  JimsProxy.exe        the proxy (self-contained - no .NET install needed)
  HermesProxy.config   settings (yes, it is named HermesProxy.config)
  CSV/                 game data tables - REQUIRED, keep it next to the exe

Keep JimsProxy.exe, HermesProxy.config and CSV/ together in one folder.


WHAT YOU NEED
-------------
A WoW Classic Era game client that you supply yourself:

  Version      1.14.2
  Build        42597
  Executable   WowClassic_ForCustomServers.exe

The custom-servers executable is required. A stock WowClassic.exe will only
ever connect to Blizzard's servers - no proxy can change that.

We do not distribute game files.


SETUP
-----
1. Open HermesProxy.config in a text editor and set ServerAddress to your
   server's logon address. For Kronos:

       <add key="ServerAddress" value="login.twinstar-wow.com" />

   That is the only value you need to change. In particular leave ClientBuild
   (already 42597) and ClientSeed alone.

2. In your game folder, edit WTF/Config.wtf and set:

       SET portal "127.0.0.1:1119"

   1119 is BNetPort in the config. If you change one, change both.

3. Run JimsProxy.exe. Wait for this line to appear:

       Starting WorldSocket service

   That is the ready signal. Do not launch the game before you see it.

4. Launch WowClassic_ForCustomServers.exe directly, and log in with your
   normal account credentials for that server.

To stop the proxy, close its console window.


IF SOMETHING GOES WRONG
-----------------------
Client never reaches character select / "World Server is Down"
    The portal and BNetPort disagree. They must match.

Proxy exits immediately, or reports a bind/port error
    It needs four free ports on 127.0.0.1: 1119, 8084, 8086, 8081.
    Almost always this is an older proxy still running - check Task Manager
    for JimsProxy.exe and end it. A leftover proxy will happily keep serving
    your session, so you may think you are running this build when you aren't.

Login fails, or the client complains about the version
    ClientBuild must match your client exactly. 1.14.2 is 42597. Check you
    are really launching WowClassic_ForCustomServers.exe.

Upgrading from an older version
    Copy your existing AccountData/ folder across, or quest tracking starts
    from scratch.

Antivirus complains
    It is an ~80 MB unsigned self-contained .NET binary that opens listening
    sockets, which draws false positives. You can build from source instead:
    https://github.com/jameopotato/jimsproxy

Reporting a bug
    Set DebugOutput=true in the config, reproduce the problem, and attach
    Logs/jimsproxy-*.jsonl to your report.


License: GPL v3. Source: https://github.com/jameopotato/jimsproxy
