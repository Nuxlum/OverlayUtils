OverlayUtils
============

(Italiano più sotto)

A panel on top of your screen (and on top of games) for gamepad, keyboard
or mouse: monitor setups, Home key remapping, and more as the port to .NET
progresses.

START
-----
1. Extract the whole zip to a folder you keep (not inside the zip itself).
2. Double-click OverlayUtils.exe. Its icon appears near the clock.
   If Windows shows "Windows protected your PC": More info -> Run anyway.
3. Right-click the icon for the menu: language, start with Windows, etc.

Do not start OverlayUtils.UIAccess.exe yourself: Windows refuses to run it
until full mode is enabled (see below). OverlayUtils starts it when needed.

OPENING THE OVERLAY
-------------------
Press Shift+F1, or left-click the tray icon. Press again (or click outside)
to close it.
Inside: Up/Down move, Left/Right change a value, Enter selects (Power options
ask for a second Enter within 3 seconds), Tab / PgUp / PgDn switch tab,
F2 opens Settings, Esc goes back or closes. The mouse works too.
Settings has size, side of the screen, language and the full mode switch
(see below), so you can enable full mode from the overlay as well.

With a gamepad (Xbox, 8BitDo, DualShock 4, DualSense, Switch Pro, or
Moonlight's virtual pad): Back+RB opens and closes the overlay. Inside,
D-pad or left stick move, A selects, B goes back, LT/RT switch tab, hold
Back for Settings. With the overlay closed, Back+D-pad Up / Down switch to
the TV / desk monitor profile (Settings -> Pad combos turns this off).

OVERLAY OVER FULLSCREEN GAMES (optional)
----------------------------------------
By default the overlay works over the desktop, windows, borderless games and
Steam Big Picture. To show it over fullscreen games too:
tray menu -> Overlay over fullscreen games -> Enable...
(or Settings in the overlay).

Windows asks for administrator permission once. OverlayUtils then trusts the
certificate it is signed with on this PC, copies itself to Program Files and
restarts from there. Trusting a certificate means Windows will accept anything
signed with it: only do this if you trust whoever gave you OverlayUtils.
The same menu has Disable..., which undoes everything.

MONITOR SETUPS (optional)
-------------------------
Needs Monitor Profile Switcher, free, from https://monitorprofileswitcher.com/
Save your setups with it and they appear in the Monitor menu.
Ctrl+Alt+Down loads the Desk profile, Ctrl+Alt+Up the TV profile.

REMOVING IT
-----------
If full mode is on, turn it off first (tray menu). Then Exit from the tray
menu and delete the folder. Settings are in %APPDATA%\OverlayUtils.

Coming from AppUtili? Your settings are copied automatically on first start,
and the tray menu offers "Remove the old AppUtili..." while it is installed.


------------------------------------------------------------------------------

OverlayUtils
============

Un pannello sopra lo schermo (anche sopra i giochi) da usare con il pad, la
tastiera o il mouse: setup dei monitor, rimappatura del tasto Home e altro,
man mano che procede il passaggio a .NET.

AVVIO
-----
1. Estrai tutto lo zip in una cartella che tieni (non avviare dallo zip).
2. Doppio clic su OverlayUtils.exe. L'icona compare vicino all'orologio.
   Se compare "PC protetto da Windows": Ulteriori informazioni -> Esegui
   comunque.
3. Tasto destro sull'icona per il menu: lingua, avvio con Windows, ecc.

Non avviare tu OverlayUtils.UIAccess.exe: Windows si rifiuta di eseguirlo
finché non attivi la modalità completa (vedi sotto). Lo avvia OverlayUtils
quando serve.

APRIRE L'OVERLAY
----------------
Premi Maiusc+F1, oppure clic sinistro sull'icona. Premi di nuovo (o clicca
fuori) per chiuderlo.
Dentro: Su/Giù si muovono, Sinistra/Destra cambiano un valore, Invio sceglie
(le opzioni di spegnimento chiedono un secondo Invio entro 3 secondi),
Tab / Pag su / Pag giù cambiano scheda, F2 apre le Impostazioni, Esc torna
indietro o chiude. Funziona anche con il mouse.
Le Impostazioni hanno dimensione, lato dello schermo, lingua e l'interruttore
della modalità completa (vedi sotto): si può attivare anche dall'overlay.

Con un controller (Xbox, 8BitDo, DualShock 4, DualSense, Switch Pro, o il pad
virtuale di Moonlight): Back+RB apre e chiude l'overlay. Dentro, croce o stick
sinistro si muovono, A sceglie, B torna indietro, LT/RT cambiano scheda,
tenere premuto Back apre le Impostazioni. A overlay chiuso, Back+croce Su /
Giù passano al profilo monitor TV / Scrivania (Impostazioni -> Combo del pad
li disattiva).

OVERLAY SOPRA I GIOCHI A SCHERMO INTERO (facoltativo)
-----------------------------------------------------
Di base l'overlay funziona sopra desktop, finestre, giochi in finestra senza
bordi e Steam Big Picture. Per vederlo anche sopra i giochi a schermo intero:
menu dell'icona -> Overlay sopra i giochi a schermo intero -> Attiva...
(oppure Impostazioni nell'overlay).

Windows chiede una volta i permessi di amministratore. OverlayUtils rende
attendibile su questo PC il certificato con cui è firmato, si copia in
Programmi e riparte da lì. Rendere attendibile un certificato significa che
Windows accetterà qualsiasi cosa firmata con esso: fallo solo se ti fidi di
chi ti ha dato OverlayUtils. Nello stesso menu c'è Disattiva..., che annulla
tutto.

SETUP DEI MONITOR (facoltativo)
-------------------------------
Serve Monitor Profile Switcher, gratuito, da https://monitorprofileswitcher.com/
Salva i tuoi setup con quel programma e compariranno nel menu Monitor.
Ctrl+Alt+Giù carica il profilo Scrivania, Ctrl+Alt+Su il profilo TV.

RIMOZIONE
---------
Se la modalità completa è attiva, disattivala prima (menu dell'icona). Poi
Esci dal menu e cancella la cartella. Le impostazioni sono in
%APPDATA%\OverlayUtils.

Arrivi da AppUtili? Le impostazioni vengono copiate da sole al primo avvio, e
finché è installata il menu offre "Rimuovi la vecchia AppUtili...".
