# OverlayUtils

*[Read in English](README.md)*

Un'utility nell'area di notifica di Windows con un overlay da usare con il
pad, la tastiera o il mouse, che può stare sopra i giochi a schermo intero:

- **Setup dei monitor** - passa da un layout salvato all'altro (es.
  scrivania / TV) con [Monitor Profile Switcher](https://monitorprofileswitcher.com/),
  `Ctrl+Alt+Giù` / `Ctrl+Alt+Su` o il menu dell'icona
- **Rimappatura del tasto Home** - il tasto "Browser Home" della tastiera
  diventa il tasto Windows o Alt+Tab
- **Overlay** *(in fase di passaggio)* - controlli di sistema, audio e media,
  libreria giochi e Discord, con qualsiasi controller supportato da SDL
- **Italiano e inglese**, secondo la lingua di Windows o scelta dal menu

> **Stato:** OverlayUtils sta passando da Python a .NET, per fasi. La fase 1
> (icona, scorciatoie, setup dei monitor, le due modalità) è scritta;
> l'overlay e il resto seguiranno. Vedi [docs/PORTING.md](docs/PORTING.md)
> (in inglese). Fino ad allora la versione Python completa resta in
> `reference/python/`.

## Due modalità

| | Standard | Completa |
|---|---|---|
| Installazione | estrai lo zip e avvia `OverlayUtils.exe` | un clic nel menu, una richiesta di amministratore |
| Overlay sopra desktop, finestre, giochi senza bordi, Big Picture | sì | sì |
| Overlay sopra i giochi a **schermo intero** | no | sì |
| Certificato installato sul PC | no | sì (rimosso quando la disattivi) |

La modalità completa richiede il permesso **UIAccess** di Windows, che Windows
concede solo a programmi firmati e installati in Programmi. OverlayUtils se ne
occupa quando scegli *Overlay sopra i giochi a schermo intero -> Attiva...*
e annulla tutto con *Disattiva...*. Rendere attendibile un certificato
significa che Windows accetta qualsiasi cosa firmata con esso: attiva la
modalità completa solo con versioni ricevute da qualcuno di cui ti fidi.

## Uso

Vedi [README.txt](README.txt) (lo stesso file è nello zip).

Arrivi da **AppUtili**? Le impostazioni e l'accesso a Discord vengono
copiati da soli al primo avvio, e il menu dell'icona propone di rimuovere la
vecchia installazione.

## Compilazione

Serve: Windows 10/11, .NET 10 SDK (oppure Visual Studio 2026 con il carico di
lavoro Sviluppo di applicazioni desktop .NET).

```powershell
# una volta: crea la tua chiave di firma (non servono permessi di amministratore)
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\certificate.ps1

# compila, firma e crea lo zip -> dist\OverlayUtils e dist\OverlayUtils-<versione>.zip
powershell -NoProfile -ExecutionPolicy Bypass -File scripts\build.ps1
```

`-NoSign` compila senza certificato (solo modalità standard). Per lo sviluppo
apri `OverlayUtils.sln` ed esegui il progetto `OverlayUtils`; le verifiche
della libreria principale girano ovunque:

```
dotnet run --project tests/OverlayUtils.SelfTest
```

Chi compila firma con la propria chiave, così nessuno deve fidarsi del
certificato di un altro. Una build data ad altri come zip chiede loro di
fidarsi del *tuo* certificato quando attivano la modalità completa.

## Guida al repository

[CLAUDE.md](CLAUDE.md) (in inglese) è il punto di partenza per chi
contribuisce: struttura, convenzioni e dove è documentato ogni argomento.
