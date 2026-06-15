using System.IO;
using UnityEngine;

public class FPSLogger : MonoBehaviour
{
    private string filePath;
    private StreamWriter writer;
    private int frameCount = 0;

    void Start() {
        // Salva il file nella cartella del progetto (o nella cartella di installazione su build)
        filePath = Path.Combine(Application.dataPath, "fps_log.txt");

        // Sovrascrive il file se esiste già e scrive l'intestazione
        writer = new StreamWriter(filePath, false);
        writer.WriteLine("Frame,FPS");

        Debug.Log($"[FPSLogger] Sto salvando i dati in: {filePath}");
    }

    void Update() {
        frameCount++;

        // Calcola gli FPS istantanei di questo frame
        // Usiamo unscaledDeltaTime per ignorare eventuali alterazioni del Time.timeScale (es. pausa o slow motion)
        float currentFPS = 1f / Time.unscaledDeltaTime;

        // Scrive nel file nel formato: NumeroFrame,ValoreFPS
        // Usiamo il punto come separatore decimale per evitare problemi di lettura successivi
        writer.WriteLine($"{frameCount},{currentFPS.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
    }

    void OnApplicationQuit() {
        // Chiude correttamente il file quando chiudi il gioco o l'editor
        if (writer != null) {
            writer.Flush();
            writer.Close();
            Debug.Log("[FPSLogger] File salvato e chiuso con successo.");
        }
    }
}