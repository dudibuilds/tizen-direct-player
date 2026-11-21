using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace Ru7100.Preview
{
    /// UI feedback from the browser preview: the user pins notes on the screen (feedback mode), each with the node
    /// under the click, the screen's text snapshot and a headless-Chrome screenshot with the pin drawn. "Send all"
    /// writes one batch folder under local-data/ui-feedback/ (feedback.md + PNGs + feedback.json) for the agent.
    internal sealed class FeedbackInbox
    {
        internal sealed class Note
        {
            public int Id { get; set; }
            public DateTime At { get; set; }
            public int X { get; set; }
            public int Y { get; set; }
            public string NodeKey { get; set; }
            public string NodeText { get; set; }
            public string Comment { get; set; }
            public string Screen { get; set; }      // UiText snapshot when the note was made
            public string Screenshot { get; set; }  // file name inside the pending folder (may be null)
        }

        private readonly object gate = new object();
        private readonly string root;
        private readonly string pendingDir;
        private readonly int port;
        private List<Note> notes = new List<Note>();
        private int nextId = 1;

        public FeedbackInbox(string projectRoot, int port)
        {
            root = Path.Combine(projectRoot, "local-data", "ui-feedback");
            pendingDir = Path.Combine(root, "pending");
            this.port = port;
            Directory.CreateDirectory(pendingDir);
            var saved = Path.Combine(pendingDir, "notes.json");
            if (File.Exists(saved))
            {
                try
                {
                    notes = JsonConvert.DeserializeObject<List<Note>>(File.ReadAllText(saved)) ?? new List<Note>();
                    nextId = notes.Count == 0 ? 1 : notes.Max(n => n.Id) + 1;
                }
                catch (Exception) { notes = new List<Note>(); }
            }
        }

        public object List()
        {
            lock (gate)
                return notes.Select(n => new { n.Id, n.X, n.Y, n.NodeKey, n.Comment, hasShot = n.Screenshot != null }).ToList();
        }

        public Note Add(int x, int y, string nodeKey, string nodeText, string comment, string screen)
        {
            Note note;
            lock (gate)
            {
                note = new Note { Id = nextId++, At = DateTime.Now, X = x, Y = y, NodeKey = nodeKey, NodeText = nodeText, Comment = comment, Screen = screen };
                notes.Add(note);
                Save();
            }
            _ = Task.Run(() => Capture(note));
            return note;
        }

        public bool Remove(int id)
        {
            lock (gate)
            {
                var note = notes.FirstOrDefault(n => n.Id == id);
                if (note == null)
                    return false;
                notes.Remove(note);
                if (note.Screenshot != null)
                    try { File.Delete(Path.Combine(pendingDir, note.Screenshot)); } catch (Exception) { }
                Save();
                return true;
            }
        }

        public void Update(int id, string comment)
        {
            lock (gate)
            {
                var note = notes.FirstOrDefault(n => n.Id == id);
                if (note == null)
                    return;
                note.Comment = comment;
                Save();
            }
        }

        /// Writes all pending notes as one batch folder and clears the list. Returns the folder, or null when empty.
        public string Send()
        {
            List<Note> batch;
            lock (gate)
            {
                if (notes.Count == 0)
                    return null;
                batch = notes;
                notes = new List<Note>();
                nextId = 1;
            }
            var folder = Path.Combine(root, "batch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(folder);
            var md = new StringBuilder();
            md.Append("# UI feedback batch ").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm")).Append(" (").Append(batch.Count).Append(" notes)\n\n");
            var number = 0;
            foreach (var note in batch)
            {
                number++;
                string shot = null;
                if (note.Screenshot != null && File.Exists(Path.Combine(pendingDir, note.Screenshot)))
                {
                    shot = $"note-{number:00}.png";
                    File.Move(Path.Combine(pendingDir, note.Screenshot), Path.Combine(folder, shot));
                }
                md.Append("## ").Append(number).Append(". ").Append(note.Comment?.Trim()).Append("\n\n");
                md.Append("- Where: (").Append(note.X).Append(", ").Append(note.Y).Append(") on the 1920×1080 stage");
                if (!string.IsNullOrEmpty(note.NodeKey))
                    md.Append(", node `").Append(note.NodeKey).Append('`');
                if (!string.IsNullOrEmpty(note.NodeText))
                    md.Append(" — \"").Append(note.NodeText.Replace("\n", " ").Trim()).Append('"');
                md.Append('\n');
                if (shot != null)
                    md.Append("- Screenshot: ").Append(shot).Append(" (red pin = click)\n");
                md.Append("\n<details><summary>Screen state</summary>\n\n```\n").Append(Trim(note.Screen)).Append("\n```\n</details>\n\n");
                note.Screenshot = shot;
            }
            File.WriteAllText(Path.Combine(folder, "feedback.md"), md.ToString());
            File.WriteAllText(Path.Combine(folder, "feedback.json"), JsonConvert.SerializeObject(batch, Formatting.Indented));
            lock (gate)
                Save();
            File.AppendAllText(Path.Combine(root, "inbox.log"), DateTime.Now.ToString("O") + " " + folder + " " + batch.Count + " notes\n");
            return folder;
        }

        /// Screen state, each line capped (catalog rows list dozens of titles).
        private static string Trim(string screen) => string.Join("\n", (screen ?? "").TrimEnd().Split('\n')
            .Select(line => line.Length > 220 ? line.Substring(0, 217) + "…" : line));

        private void Save() => File.WriteAllText(Path.Combine(pendingDir, "notes.json"), JsonConvert.SerializeObject(notes, Formatting.Indented));

        /// Headless Chrome screenshot of the settled layout with a red pin at the click.
        private void Capture(Note note)
        {
            const string chrome = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            if (!File.Exists(chrome))
                return;
            var file = $"pending-{note.Id:000}-{Guid.NewGuid():N}.png";
            var path = Path.Combine(pendingDir, file);
            var profile = Path.Combine(Path.GetTempPath(), "ru7100-feedback-chrome");
            var url = $"http://127.0.0.1:{port}/snapshot.html?pin={note.X},{note.Y}&n={note.Id}";
            var psi = new ProcessStartInfo(chrome, $"--headless=new --disable-gpu --hide-scrollbars --user-data-dir=\"{profile}\" --window-size=1920,1080 --virtual-time-budget=4000 --screenshot=\"{path}\" \"{url}\"")
            {
                RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
            };
            try
            {
                using (var process = Process.Start(psi))
                {
                    if (!process.WaitForExit(30000))
                        try { process.Kill(); } catch (Exception) { }
                }
                if (!File.Exists(path))
                    return;
                lock (gate)
                {
                    var current = notes.FirstOrDefault(n => n.Id == note.Id && n.At == note.At);
                    if (current == null)
                    {
                        try { File.Delete(path); } catch (Exception) { }
                        return;
                    }
                    current.Screenshot = file;
                    Save();
                }
            }
            catch (Exception error) { Console.WriteLine("feedback screenshot: " + error.Message); }
        }
    }
}
