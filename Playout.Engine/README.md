# Playout.Engine

Die Engine stellt ihre API vorläufig nur auf `http://127.0.0.1:5187` bereit. Sie besitzt derzeit keine Authentifizierung und ist nicht für einen ungeschützten Netzwerkbetrieb vorgesehen.

## Geräteerkennung

Für die Geräteerkennung unter Windows werden folgende native Bibliotheken verwendet:

- `bass.dll`
- `basswasapi.dll` für WASAPI
- `bassasio.dll` für ASIO

Die offiziellen Windows-DLLs für x86 und x64 liegen unter `runtimes/win-x86/native` beziehungsweise `runtimes/win-x64/native`. Sie werden beim Build und Publish samt der Herstellerhinweise unter `third-party/bass` übernommen. Die Engine lädt die DLLs passend zur Architektur ihres Prozesses; Windows ARM64 wird derzeit nicht unterstützt.

BASS und BASSASIO haben eigene Lizenzbedingungen. Vor kommerzieller Nutzung und Weitergabe müssen die Herstellerhinweise in `third-party/bass` geprüft und gegebenenfalls entsprechende Lizenzen erworben werden. Installierte und funktionierende Gerätetreiber werden vorausgesetzt.

`GET /api/audio-devices` liefert für WASAPI und ASIO jeweils `available`, gegebenenfalls `error` und die gefundenen Geräte. Nicht verfügbare DLLs, Architekturfehler und Fehler der nativen Geräteerkennung werden pro Backend gemeldet. WASAPI-Eingänge und Loopback-Geräte werden nicht als Ausgänge aufgeführt. Die WASAPI-Kanalanzahl und Abtastrate beziehen sich auf den Shared Mode; sie gelten nicht automatisch für den Exclusive Mode. Bei ASIO sind Verfügbarkeit und Kanalanzahl eines Treibers ohne Initialisierung nicht bekannt.

Für die Routingkonfiguration ist die Geräte-ID aus der Antwort zu verwenden, nicht `nativeIndex`. Der native Index kann sich ändern. Die ASIO-ID wird aus Treibername und -datei gebildet und kann sich ändern, wenn sich diese Angaben ändern.

## Routing

`GET /api/audio-routing` liest die Konfiguration, `PUT /api/audio-routing` ersetzt sie. Die Kanalindizes sind nullbasiert. Die Engine speichert sie derzeit in `%LOCALAPPDATA%\Playout\Engine\audio-routing.json`. Eine konfigurierte Geräte-ID bedeutet nicht, dass das Gerät verfügbar ist oder der Kanal tatsächlich existiert.

Mehrere Ausgänge dürfen dieselben Kanäle desselben Geräts verwenden. Ihre Signale werden addiert und auf den Bereich von -1 bis 1 begrenzt. Ausgänge mit `device: null` bleiben unverbunden. Das erlaubt auch die Nutzung nur eines Interfaces für mehrere Programmausgänge. Bei ASIO müssen sich die benötigten Kanäle vom Treiber zu einer Gruppe verbinden lassen.

## Wiedergabe

Die Wiedergabe wird über die lokale API gesteuert:

1. Mit `PUT /api/audio-routing` die Geräte und Kanäle zuordnen.
2. Mit `POST /api/audio/initialize` alle konfigurierten Geräte initialisieren. Ist eine Zuordnung nicht verfügbar, schlägt die Initialisierung insgesamt fehl und bereits geöffnete Geräte werden wieder freigegeben. Ohne konfigurierte Geräte ist die Initialisierung möglich, aber kein Deck kann spielen.
3. Mit `POST /api/decks/{deckId}/load` und einem JSON-Body wie `{"path":"C:\\Musik\\Titel.mp3"}` eine lokale Audiodatei laden.
4. Mit `POST /api/decks/{deckId}/play`, `/pause` oder `/stop` das Deck steuern. `stop` setzt auf den Anfang zurück; nach dem Ende startet `play` den Titel erneut. Ein nicht verbundenes Deck kann nicht gestartet werden.
5. `GET /api/decks` liefert Deckzustände und Dekodierungsfehler, `GET /api/audio/status` zusätzlich initialisierte Geräte und Callbackfehler. `POST /api/audio/shutdown` beendet die Ausgabe und gibt alle Dateien und Geräte frei.

Routingänderungen sind nur nach `shutdown` möglich; anschließend muss die Ausgabe erneut initialisiert werden. WASAPI verwendet den Shared Mode und dessen Mixformat. ASIO verwendet die aktuelle Abtastrate des Treibers. Mono- und Stereo-Dateien werden durch BASS dekodiert und bei unterschiedlichen Abtastraten linear umgerechnet. Das ist keine bandbegrenzte Resampling-Implementierung. Die Wiedergabe läuft in der Engine auch ohne verbundene Oberfläche; die GUI bietet dafür noch keine Bedienung.

Die native Ausgabe und das Verhalten bei Geräteausfall müssen mit den eingesetzten WASAPI- und ASIO-Treibern geprüft werden. CUE/PFL-Vorhören, automatische Spiellistenausspielung und AOT-Steuerung sind noch nicht implementiert.
