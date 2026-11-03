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
Also with the overlay closed: Back+D-pad Left / Right lower / raise the
volume and Back+Y mutes the microphone (a small indicator appears on screen).
With the overlay open, LB / RB / X are previous / next / play-pause.

AUDIO
-----
The Audio tab shows the output device, the microphone, the volume and what
is playing. Left/Right on Output or Input switch device, A on Input mutes the
microphone, A on Volume mutes, Left/Right on Now playing skip tracks.
Settings -> Audio has one priority list for outputs and one for microphones:
select a device, A picks it up, Up/Down move it, A drops it (B cancels), Y
removes a device that is not connected. Whenever a device is plugged in or
removed, Windows is switched to the highest device in the list that is
connected (turn this off per list in Settings). A choice made by hand in the
Audio tab holds until the next device change. "Max volume" limits the volume
of an output device from the overlay and the pad.

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
Sempre a overlay chiuso: Back+croce Sinistra / Destra abbassano / alzano il
volume e Back+Y silenzia il microfono (compare un piccolo indicatore).
Con l'overlay aperto, LB / RB / X sono traccia precedente / successiva /
play-pausa.

AUDIO
-----
La scheda Audio mostra l'uscita, il microfono, il volume e cosa sta
suonando. Sinistra/Destra su Uscita o Ingresso cambiano dispositivo, A su
Ingresso silenzia il microfono, A su Volume silenzia, Sinistra/Destra su
In riproduzione cambiano traccia.
Impostazioni -> Audio ha una lista di priorità per le uscite e una per i
microfoni: seleziona un dispositivo, A lo solleva, Su/Giù lo spostano, A lo
lascia (B annulla), Y toglie un dispositivo non collegato. Quando colleghi o
scolleghi un dispositivo, Windows passa al primo della lista tra quelli
collegati (si disattiva per ogni lista nelle Impostazioni). Una scelta fatta
a mano nella scheda Audio vale fino al prossimo cambio di dispositivi.
"Volume massimo" limita il volume di un'uscita dall'overlay e dal pad.

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
