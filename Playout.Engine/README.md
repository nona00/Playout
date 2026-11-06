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

Die Geräteerkennung prüft nicht, ob die gespeicherten Zuordnungen verwendbar sind. Die Initialisierung von BASS, WASAPI und ASIO zur Audiowiedergabe, Mehrkanal-Mixing, CUE/PFL, die eigentliche Ausspielung und AOT-Steuerung sind noch nicht implementiert. `audioInitialized` im Status bleibt daher `false`.
