# TODO: Mehrkanal-Playout-System

Diese Liste vergleicht die Anforderungssammlung mit dem aktuellen Workspace. Ein Häkchen bedeutet, dass ein Arbeitspaket einschließlich seiner Prüfung abgeschlossen ist; vorhandene Teilimplementierungen sind unten gesondert beschrieben.

## Aktueller Stand

- Die Solution enthält `Playout.Engine` als separaten Prozess und `Playout.Gui` als WinForms-Anwendung. Die Engine bietet eine lokale HTTP-API; die GUI ist ein statisches Layout ohne Engine-Anbindung. Ein Webprojekt ist derzeit nicht in der Solution vorhanden.
- Die Engine erkennt WASAPI- und ASIO-Geräte, speichert Routing in einer lokalen JSON-Datei und bietet über die API Initialisierung, Status sowie Laden, Starten, Pausieren und Stoppen von Decks. Mehrere Ausgänge können auf dasselbe Interface und auch auf dieselben Kanäle gemischt werden; ein Ausgang kann unverbunden bleiben. Das Standardrouting enthält drei Programmausgänge und einen PFL-Ausgang.
- Die native Ausgabe ist implementiert, aber die Prüfung mit realen WASAPI- und ASIO-Geräten sowie das Verhalten bei Geräteausfall stehen aus. Der Mixer verwendet derzeit lineares Resampling und begrenzt Summensignale hart. PFL-Vorhören und eine automatische Spiellistenausspielung fehlen.
- `Playout.Engine/AotDecision.cs` enthält AOT-Entscheidungslogik, aber keine Ablaufsteuerung. Daneben existiert `Playout.Domain/AoTScheduler.cs` außerhalb der Solution; die beiden Ansätze sind noch nicht zusammengeführt. Anzeige und tatsächliche Übergänge fehlen.
- `Playout.Engine.Tests` enthält Tests für AOT, Mixing und Dekodierung, ist aber nicht in die Solution eingebunden. Der Build der Solution allein führt diese Tests nicht aus.
- Eine zentrale Datenbank, Musikverwaltung, Benutzerverwaltung, Rollen und Bedienverantwortung sind noch nicht implementiert. Die Engine-API ist derzeit auf `127.0.0.1:5187` beschränkt und hat keine Authentifizierung.

## Offene Produktentscheidungen

- [ ] Festlegen, ob mindestens vier Stereo-Ausgänge **einschließlich PFL** oder vier Programmausgänge **zusätzlich zu PFL** erforderlich sind. Aktuell sind drei Programmausgänge plus PFL voreingestellt; die Routingliste ist erweiterbar.
- [ ] Festlegen, wie PFL vom Programm akustisch getrennt wird und welche Kanalüberlappungen zulässig sind. Ein einzelnes Mehrkanalinterface kann getrennte Kanalpaare bereitstellen; bei nur einem gemeinsam genutzten Stereo-Kanalpaar ist unabhängiges Vorhören nicht möglich.
- [ ] Verhalten bei fehlenden Geräten, Gerätewechsel, Treiberfehlern und Wiedereinschalten definieren: Warnung, Fortsetzung, Wiederverbindung und Umgang mit betroffenen Decks.
- [ ] AOT-Zeitzone, Sommerzeitfälle, Überblenddauer und -kurve sowie Behandlung verspäteter AOTs, mehrerer gleichzeitiger AOTs und fehlender Titel festlegen.
- [ ] Übergabe und Entzug der Bedienverantwortung sowie Verhalten bei Verbindungsverlust und Neustart festlegen.
- [ ] Für bestätigungspflichtige Änderungen festlegen, welche laufenden oder bereits vorbereiteten Titel betroffen sein dürfen und wie abgelehnte, überholte oder konkurrierende Änderungen behandelt werden.
- [ ] Zielplattformen, unterstützte Audioformate, Anforderungen an Latenz, Audioqualität und Dauerbetrieb sowie eine visuelle Gestaltungsvorlage festlegen.

Die Nutzung eines einzigen Interfaces **oder mehrerer Geräte** ist bereits gewünscht: Alle benötigten Ausgänge sollen einem Interface zugeordnet werden können oder unverbunden bleiben. Diese Entscheidung bei der weiteren Umsetzung beibehalten.

## P0 – Audioausgabe absichern

- [ ] WASAPI Shared Mode und ASIO mit realen Treibern prüfen: Stereo- und Mehrkanalgeräte, unterschiedliche Kanalpaare, vertauschte Kanäle, gemeinsame Kanäle, mehrere Geräte und unverbundene Ausgänge. Insbesondere ASIO-Kanalgruppierung und Kanalreihenfolge überprüfen.
- [ ] Fehlerfälle und Lebenszyklus testen und gegebenenfalls beheben: nicht vorhandene Kanäle, gesperrte Geräte, fehlende DLLs, Treiberverlust, erneute Initialisierung, parallele Befehle und vollständige Freigabe. Initialisierung ist derzeit für alle konfigurierten Geräte gemeinsam erfolgreich oder fehlgeschlagen.
- [ ] Echtzeitverhalten überprüfen: Dekodierung und Sperren im Audio-Callback, Pufferallokationen, Aussetzer, Last und Langzeitstabilität messen; erkannte Ursachen beheben. Resampling-Qualität und Übersteuerungsbehandlung anhand der festgelegten Anforderungen verbessern.
- [ ] Betriebsstatus und Fehler so bereitstellen, dass ein Ausfall eines Geräts oder Decks erkennbar ist. Definiertes Wiederanlauf- und Alarmverhalten implementieren.
- [ ] Routing- und Geräteeinstellungen in der Bedienoberfläche konfigurierbar machen, einschließlich mehrerer Ausgänge pro Interface und unverbundener Ausgänge. Aktuell erfordert eine Routingänderung das Beenden und erneute Initialisieren der Audioausgabe.

**Abnahme:** Gleichzeitige Deckwiedergabe erreicht auf geprüfter Hardware ausschließlich die ausgewählten Kanäle; gemeinsame Zuordnungen mischen wie erwartet, unverbundene Ausgänge bleiben stumm und Fehler werden sichtbar gemeldet.

## P1 – Datenhaltung und gemeinsame Verträge

- [ ] Domänenmodell für Benutzer, Rollen, Bedienverantwortung, Musikquellen, Titel, Spiellisten, Elemente, AOT, Routing, Einstellungen und Wiederanlaufzustand festlegen.
- [ ] Gemeinsame Schnittstellen und Speichergrenzen zwischen Engine und Webanwendung definieren. Entscheiden, ob und wie `Playout.Domain` als Projekt eingebunden wird; doppelte AOT-Modelle vermeiden.
- [ ] Persistenzschicht für SQLite, SQL Server Express und MySQL implementieren. EF Core mit geeigneten Providern und getrennt verwalteten Migrationen ist vorgeschlagen, aber noch zu entscheiden. Einrichtung, Providerkonfiguration, Transaktionen und Schema-Upgrades dokumentieren.
- [ ] Routing und weitere Einstellungen aus der lokalen JSON-Datei in die vereinbarte Datenhaltung überführen; Migration bestehender Einstellungen und Umgang mit ungültigen Daten festlegen.
- [ ] Wiederanlaufzustand so speichern und wiederherstellen, dass nach Prozess- oder Rechnerneustart das vereinbarte Verhalten nachvollziehbar und sicher ist. Keine unbeabsichtigte automatische Ausspielung ohne festgelegte Regel.

**Abnahme:** Ein neuer und ein aktualisierter Datenbestand lassen sich mit jedem der drei Datenbankanbieter einrichten; gespeicherte Einstellungen und Planungsdaten überstehen einen Neustart.

## P1 – Musikquellen und Import

- [ ] Mehrere Quellen mit Pfad, Status und Verwaltung anlegen; Dateireferenzen und Metadaten speichern, Audiodateien in den Quellordnern belassen.
- [ ] Importläufe für neue und geänderte Dateien implementieren; bekannte Dateien zuverlässig wiedererkennen und fehlende oder fehlerhafte Dateien als solche kennzeichnen.
- [ ] Titel nach Metadaten und Verfügbarkeit durchsuchbar machen; unterstützte Formate, Zugriffsfehler und gleichzeitige Importläufe behandeln.
- [ ] Import- und Recherchefunktionen über autorisierte APIs und die Weboberfläche bereitstellen.

**Abnahme:** Mehrfache Importläufe erzeugen keine unbeabsichtigten Duplikate; Änderungen, fehlende Dateien und Fehler sind in der Recherche sichtbar.

## P1 – Spiellisten und AOT-Ausspielung

- [ ] Spiellistenmodell und APIs für Tages-/Stundenraster, Reihenfolge, Titelreferenzen und optionale AOT-Uhrzeiten pro Element erstellen. Das AOT-Datum aus dem Spiellistentag ableiten.
- [ ] Planung und Bestückung im Web ermöglichen, inklusive Validierung von Verfügbarkeit, Uhrzeit und Zeitzone.
- [ ] Engine-Ablaufsteuerung implementieren: Spielliste laden, Titel vorbereiten, Decks belegen, Wiedergabeende und Fehler behandeln und unabhängig von verbundenen Oberflächen fortfahren.
- [ ] Eine maßgebliche AOT-Entscheidungslogik festlegen und an die Ablaufsteuerung anbinden. Vorhandene Engine-Tests und die separate Domain-Implementierung berücksichtigen.
- [ ] Übergänge tatsächlich ausführen: gleichzeitiges Ein-/Ausblenden, harter Stopp und Warten auf das Ende des laufenden Titels. Verspätungen und Sonderfälle nach den getroffenen Entscheidungen behandeln.
- [ ] Nächste AOT, Restzeit und aktuellen Ausspielzustand über die Engine-API bereitstellen und in beiden Oberflächen anzeigen.

**Abnahme:** Eine im Web geplante Liste spielt auch nach Trennung der Oberflächen weiter; alle drei AOT-Verhaltensweisen und die Countdown-Anzeige sind anhand reproduzierbarer Zeit- und Decktests überprüft.

## P1 – Benutzer, Rollen und Übernahme von Änderungen

- [ ] Zentrale Anmeldung und Autorisierung für beide Oberflächen und die Engine-Anbindung einführen. Rollen für Administration, Planung, Ausspielung und lesenden Zugriff mit konkreten Berechtigungen abbilden.
- [ ] Aktuelle Bedienverantwortung getrennt von Rollen speichern und atomar erwerben, übertragen und freigeben. Verbindungsverlust und Wiederanlauf gemäß Entscheidung behandeln.
- [ ] Spiellisten versionieren und konkurrierende Bearbeitungen erkennen, statt Änderungen still zu überschreiben.
- [ ] Automatische und bestätigungspflichtige Übernahme implementieren. Ausstehende Änderungen anzeigen; nur die aktuell verantwortliche Person darf die passende Version bestätigen. Veraltete oder doppelte Bestätigungen sicher behandeln.
- [ ] Vertrauensgrenzen für Webzugriff und Engine definieren. Vor Netzwerkfreigabe Authentifizierung, Autorisierung, sichere Transportverbindungen und Schutz der Steuerendpunkte sicherstellen; die derzeit ungeschützte lokale API nicht unverändert öffentlich bereitstellen.

**Abnahme:** Rollentests verweigern unberechtigte Aktionen; bei konkurrierenden Änderungen geht keine Version unbemerkt verloren und nur die verantwortliche Person kann eine noch aktuelle Änderung übernehmen.

## P2 – Oberflächen und CUE/PFL

- [ ] Gemeinsamen Engine-Client und Zustandsaktualisierung für WinForms und Web bereitstellen, einschließlich Verbindungsabbruch und Wiederverbindung. Beide Oberflächen zeigen denselben Zustand.
- [ ] WinForms-Platzhalter mit Spielliste, Suche/CUE, zwei bedienbaren Decks und AOT-Anzeige verbinden. Status, Fehler und ausstehende Bestätigungen anzeigen.
- [ ] Separate Webanwendung erstellen und in die Solution aufnehmen: Planung, Recherche, Import, Verwaltung und Anzeige/Bedienung entsprechend den Berechtigungen.
- [ ] CUE-Quelle und unabhängige PFL-Wiedergabe implementieren: Titel vorhören und steuern, ohne die Programmausspielung zu verändern. PFL nach der festgelegten Trennungsregel routen; ein eigenes Interface darf nicht vorausgesetzt werden.
- [ ] Modernes, konsistentes Design anhand einer abgestimmten Vorlage ausarbeiten; Tastaturbedienung, Skalierung und verständliche Fehlerzustände berücksichtigen.

**Abnahme:** Beide Oberflächen spiegeln Engine-Änderungen und überstehen Wiederverbindungen; CUE ist nur auf dem vorgesehenen PFL-Signal hörbar und unterbricht kein Programm.

## P2 – Tests, Betrieb und Auslieferung

- [ ] `Playout.Engine.Tests` in die Solution und automatisierte Testausführung aufnehmen. Testhost-/Umgebungsprobleme beheben und Unit-, API-, Integrations- und End-to-End-Tests regelmäßig ausführen.
- [ ] Tests für Routing, PFL-Isolation, Mehrkanal-Mixing, Audiofehler, AOT-Zeitgrenzen und Sommerzeit, konkurrierende Bearbeitung, Rollen, Bedienverantwortung und Wiederanlauf ergänzen. Datenbanktests für alle drei Provider vorsehen.
- [ ] Hardwaretestmatrix für WASAPI/ASIO, x86/x64 und die tatsächlich unterstützten Geräte dokumentieren; Dauerlauf, Latenz und Geräteausfälle mit messbaren Kriterien prüfen.
- [ ] Start, Konfiguration, Logs, Diagnose, Backup und Wiederherstellung sowie Installation und Updates für Engine, WinForms und Web dokumentieren. Unabhängigkeit der Engine von den Clients im Betrieb testen.
- [ ] BASS-/BASSASIO-Lizenzbedingungen für Nutzung und Weitergabe prüfen; unterstützte Architekturen und native Abhängigkeiten für Build und Deployment dokumentieren.
