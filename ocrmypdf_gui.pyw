"""
OCRmyPDF GUI - a simple Tkinter front-end for the ocrmypdf command line tool.

Run with pythonw.exe for a console-free desktop app:
    pythonw ocrmypdf_gui.pyw
"""

import json
import os
import queue
import subprocess
import threading
import tkinter as tk
import uuid
from pathlib import Path
from tkinter import filedialog, messagebox, ttk

APP_TITLE = "OCRmyPDF GUI"
CONFIG_PATH = Path(os.getenv("LOCALAPPDATA", Path.home())) / APP_TITLE / "settings.json"
DEFAULT_LANGS = ["eng"]
FALLBACK_LANGS = [
    "eng", "spa", "fra", "deu", "ita", "por", "nld", "rus", "chi_sim",
    "chi_tra", "jpn", "kor", "ara", "hin", "pol", "swe", "tur", "ukr",
]


def find_ocrmypdf() -> str:
    from shutil import which

    exe = which("ocrmypdf") or which("ocrmypdf.exe")
    if exe:
        return exe
    # common scoop install location as a fallback
    candidate = Path.home() / "scoop" / "apps" / "python" / "current" / "Scripts" / "ocrmypdf.exe"
    if candidate.exists():
        return str(candidate)
    return "ocrmypdf"


def list_tesseract_langs() -> list[str]:
    from shutil import which

    exe = which("tesseract") or which("tesseract.exe")
    if not exe:
        return FALLBACK_LANGS
    try:
        out = subprocess.run(
            [exe, "--list-langs"],
            capture_output=True,
            text=True,
            timeout=10,
            creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
        )
        lines = [ln.strip() for ln in out.stdout.splitlines() if ln.strip()]
        langs = [ln for ln in lines if not ln.lower().startswith("list of")]
        return sorted(langs) if langs else FALLBACK_LANGS
    except Exception:
        return FALLBACK_LANGS


class OcrJob:
    def __init__(self, input_path: Path, output_path: Path, args: list[str]):
        self.input_path = input_path
        self.output_path = output_path
        self.args = args


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title(APP_TITLE)
        self.geometry("900x720")
        self.minsize(760, 600)
        self.protocol("WM_DELETE_WINDOW", self.on_close)

        self.ocrmypdf_exe = find_ocrmypdf()
        self.available_langs = list_tesseract_langs()

        self.input_files: list[Path] = []
        self.log_queue: queue.Queue = queue.Queue()
        self.worker_thread: threading.Thread | None = None
        self.current_process: subprocess.Popen | None = None
        self.cancel_requested = False
        self.last_output_dir: Path | None = None
        self.saved_widget_states: dict[tk.Widget, str] = {}

        self._build_ui()
        self._load_settings()
        self.after(100, self._poll_log_queue)

    # ---------- UI construction ----------

    def _build_ui(self):
        pad = {"padx": 8, "pady": 6}

        self.notebook = ttk.Notebook(self)
        self.notebook.pack(fill="both", expand=True, padx=8, pady=(8, 4))
        files_tab = ttk.Frame(self.notebook)
        settings_tab = ttk.Frame(self.notebook)
        log_tab = ttk.Frame(self.notebook)
        self.notebook.add(files_tab, text="Files & Output")
        self.notebook.add(settings_tab, text="OCR Settings")
        self.notebook.add(log_tab, text="Log & Results")

        # --- Input files ---
        files_frame = ttk.LabelFrame(files_tab, text="Input PDF files")
        files_frame.pack(fill="both", expand=False, **pad)

        list_row = ttk.Frame(files_frame)
        list_row.pack(fill="both", expand=True, padx=6, pady=6)

        self.files_listbox = tk.Listbox(list_row, selectmode=tk.EXTENDED, height=6)
        self.files_listbox.pack(side="left", fill="both", expand=True)
        scrollbar = ttk.Scrollbar(list_row, orient="vertical", command=self.files_listbox.yview)
        scrollbar.pack(side="left", fill="y")
        self.files_listbox.config(yscrollcommand=scrollbar.set)

        files_btns = ttk.Frame(files_frame)
        files_btns.pack(side="left", fill="y", padx=6, pady=6)
        ttk.Button(files_btns, text="Add Files...", command=self.add_files).pack(fill="x", pady=2)
        ttk.Button(files_btns, text="Add Folder...", command=self.add_folder).pack(fill="x", pady=2)
        ttk.Button(files_btns, text="Remove Selected", command=self.remove_selected).pack(fill="x", pady=2)
        ttk.Button(files_btns, text="Clear All", command=self.clear_files).pack(fill="x", pady=2)

        # --- Output settings ---
        out_frame = ttk.LabelFrame(files_tab, text="Output")
        out_frame.pack(fill="x", **pad)

        self.overwrite_var = tk.BooleanVar(value=False)
        ttk.Checkbutton(
            out_frame,
            text="Overwrite original files (OCR in place, no suffix, no separate folder)",
            variable=self.overwrite_var,
            command=self._update_output_state,
        ).grid(row=0, column=0, columnspan=3, sticky="w", padx=6, pady=(6, 2))

        ttk.Label(out_frame, text="Output folder:").grid(row=1, column=0, sticky="w", padx=6)
        self.output_dir_var = tk.StringVar(value="")
        self.output_dir_entry = ttk.Entry(out_frame, textvariable=self.output_dir_var)
        self.output_dir_entry.grid(row=1, column=1, sticky="ew", padx=6)
        self.output_dir_btn = ttk.Button(out_frame, text="Browse...", command=self.browse_output_dir)
        self.output_dir_btn.grid(row=1, column=2, padx=6)

        ttk.Label(out_frame, text="Filename suffix:").grid(row=2, column=0, sticky="w", padx=6, pady=(2, 6))
        self.suffix_var = tk.StringVar(value="_ocr")
        self.suffix_entry = ttk.Entry(out_frame, textvariable=self.suffix_var, width=15)
        self.suffix_entry.grid(row=2, column=1, sticky="w", padx=6, pady=(2, 6))

        out_frame.columnconfigure(1, weight=1)

        # --- OCR options ---
        opts_frame = ttk.LabelFrame(settings_tab, text="Basic and advanced OCR options")
        opts_frame.pack(fill="x", **pad)

        # languages
        ttk.Label(opts_frame, text="Languages (ctrl/shift-click for multiple):").grid(
            row=0, column=0, sticky="nw", padx=6, pady=6
        )
        lang_row = ttk.Frame(opts_frame)
        lang_row.grid(row=0, column=1, columnspan=3, sticky="w", padx=6, pady=6)
        self.lang_listbox = tk.Listbox(lang_row, selectmode=tk.MULTIPLE, height=5, exportselection=False, width=20)
        for lang in self.available_langs:
            self.lang_listbox.insert(tk.END, lang)
        if "eng" in self.available_langs:
            self.lang_listbox.selection_set(self.available_langs.index("eng"))
        self.lang_listbox.pack(side="left")
        lang_scroll = ttk.Scrollbar(lang_row, orient="vertical", command=self.lang_listbox.yview)
        lang_scroll.pack(side="left", fill="y")
        self.lang_listbox.config(yscrollcommand=lang_scroll.set)

        # OCR mode
        ttk.Label(opts_frame, text="Text handling:").grid(row=1, column=0, sticky="w", padx=6, pady=4)
        self.mode_var = tk.StringVar(value="skip-text")
        modes = [
            ("Normal - skip pages that already have text (--skip-text)", "skip-text"),
            ("Force OCR - rasterize and OCR every page (--force-ocr)", "force-ocr"),
            ("Redo OCR - replace existing OCR text layers (--redo-ocr)", "redo-ocr"),
            ("Strict - fail if any page already has text", "strict"),
        ]
        for i, (label, val) in enumerate(modes):
            ttk.Radiobutton(opts_frame, text=label, variable=self.mode_var, value=val).grid(
                row=2 + i, column=0, columnspan=3, sticky="w", padx=20
            )

        # checkboxes
        chk_row = 6
        self.deskew_var = tk.BooleanVar(value=True)
        self.rotate_var = tk.BooleanVar(value=True)
        self.clean_var = tk.BooleanVar(value=False)
        self.clean_final_var = tk.BooleanVar(value=False)
        self.remove_bg_var = tk.BooleanVar(value=False)
        self.pdfa_var = tk.BooleanVar(value=True)

        ttk.Checkbutton(opts_frame, text="Deskew", variable=self.deskew_var).grid(
            row=chk_row, column=0, sticky="w", padx=20, pady=(10, 2)
        )
        ttk.Checkbutton(opts_frame, text="Auto-rotate pages", variable=self.rotate_var).grid(
            row=chk_row, column=1, sticky="w", pady=(10, 2)
        )
        ttk.Checkbutton(opts_frame, text="Clean (unpaper) before OCR", variable=self.clean_var).grid(
            row=chk_row + 1, column=0, sticky="w", padx=20
        )
        ttk.Checkbutton(opts_frame, text="Clean final output too", variable=self.clean_final_var).grid(
            row=chk_row + 1, column=1, sticky="w"
        )
        ttk.Checkbutton(opts_frame, text="Remove background", variable=self.remove_bg_var).grid(
            row=chk_row + 2, column=0, sticky="w", padx=20
        )
        ttk.Checkbutton(opts_frame, text="Output as PDF/A (archival)", variable=self.pdfa_var).grid(
            row=chk_row + 2, column=1, sticky="w"
        )

        ttk.Label(opts_frame, text="Optimize level:").grid(row=chk_row + 3, column=0, sticky="w", padx=20, pady=(4, 8))
        self.optimize_var = tk.StringVar(value="1")
        ttk.Combobox(
            opts_frame, textvariable=self.optimize_var, values=["0", "1", "2", "3"], width=5, state="readonly"
        ).grid(row=chk_row + 3, column=1, sticky="w", pady=(4, 8))

        ttk.Label(opts_frame, text="Extra arguments:").grid(row=chk_row + 4, column=0, sticky="w", padx=20)
        self.extra_args_var = tk.StringVar(value="")
        ttk.Entry(opts_frame, textvariable=self.extra_args_var).grid(
            row=chk_row + 4, column=1, columnspan=3, sticky="ew", padx=(0, 6), pady=(0, 8)
        )
        opts_frame.columnconfigure(3, weight=1)

        # --- Run controls ---
        run_frame = ttk.Frame(self)
        run_frame.pack(fill="x", **pad)
        self.run_btn = ttk.Button(
            run_frame, text="Run OCR", command=self.start_run, width=16, padding=(12, 8)
        )
        self.run_btn.pack(side="left")
        self.cancel_btn = ttk.Button(run_frame, text="Cancel", command=self.cancel_run, state="disabled")
        self.cancel_btn.pack(side="left", padx=6)
        self.open_folder_btn = ttk.Button(
            run_frame, text="Open Output Folder", command=self.open_output_folder, state="disabled"
        )
        self.open_folder_btn.pack(side="left", padx=6)

        self.progress = ttk.Progressbar(run_frame, mode="determinate", maximum=1)
        self.progress.pack(side="left", fill="x", expand=True, padx=10)

        self.status_var = tk.StringVar(value=f"Ready ({self.ocrmypdf_exe})")
        ttk.Label(self, textvariable=self.status_var, anchor="w").pack(fill="x", padx=10)

        # --- Log ---
        log_frame = ttk.LabelFrame(log_tab, text="Log")
        log_frame.pack(fill="both", expand=True, **pad)
        self.log_text = tk.Text(log_frame, height=12, wrap="word", state="disabled")
        self.log_text.pack(side="left", fill="both", expand=True, padx=(6, 0), pady=6)
        log_scroll = ttk.Scrollbar(log_frame, orient="vertical", command=self.log_text.yview)
        log_scroll.pack(side="left", fill="y", pady=6, padx=(0, 6))
        self.log_text.config(yscrollcommand=log_scroll.set)

        log_buttons = ttk.Frame(log_tab)
        log_buttons.pack(fill="x", padx=8, pady=(0, 6))
        ttk.Button(log_buttons, text="Clear Log", command=self.clear_log).pack(side="left")
        ttk.Button(log_buttons, text="Save Log...", command=self.save_log).pack(side="left", padx=6)

        self._update_output_state()

    # ---------- file list handling ----------

    def add_files(self):
        paths = filedialog.askopenfilenames(
            title="Select PDF files", filetypes=[("PDF files", "*.pdf"), ("All files", "*.*")]
        )
        for p in paths:
            path = Path(p)
            if path not in self.input_files:
                self.input_files.append(path)
                self.files_listbox.insert(tk.END, str(path))

    def add_folder(self):
        d = filedialog.askdirectory(title="Select a folder of PDF files")
        if not d:
            return
        for path in sorted(Path(d).rglob("*.pdf")):
            if path not in self.input_files:
                self.input_files.append(path)
                self.files_listbox.insert(tk.END, str(path))

    def remove_selected(self):
        for i in reversed(self.files_listbox.curselection()):
            self.files_listbox.delete(i)
            del self.input_files[i]

    def clear_files(self):
        self.files_listbox.delete(0, tk.END)
        self.input_files.clear()

    def browse_output_dir(self):
        d = filedialog.askdirectory(title="Select output folder")
        if d:
            self.output_dir_var.set(d)

    def _update_output_state(self):
        state = "disabled" if self.overwrite_var.get() else "normal"
        self.output_dir_entry.config(state=state)
        self.output_dir_btn.config(state=state)
        self.suffix_entry.config(state=state)

    # ---------- logging ----------

    def log(self, text: str):
        self.log_queue.put(text)

    def _poll_log_queue(self):
        try:
            while True:
                item = self.log_queue.get_nowait()
                if isinstance(item, tuple):
                    event, value = item
                    if event == "status":
                        completed, total, name = value
                        self.progress.config(maximum=max(total, 1), value=completed)
                        self.status_var.set(f"Processing {min(completed + 1, total)}/{total}: {name}")
                    elif event == "finished":
                        self._on_run_finished(value)
                    continue
                self.log_text.config(state="normal")
                self.log_text.insert(tk.END, item + "\n")
                self.log_text.see(tk.END)
                self.log_text.config(state="disabled")
        except queue.Empty:
            pass
        self.after(100, self._poll_log_queue)

    def clear_log(self):
        self.log_text.config(state="normal")
        self.log_text.delete("1.0", tk.END)
        self.log_text.config(state="disabled")

    def save_log(self):
        path = filedialog.asksaveasfilename(
            title="Save log", defaultextension=".txt", filetypes=[("Text files", "*.txt")]
        )
        if not path:
            return
        try:
            Path(path).write_text(self.log_text.get("1.0", "end-1c"), encoding="utf-8")
        except OSError as exc:
            messagebox.showerror(APP_TITLE, f"Could not save the log:\n{exc}")

    # ---------- job building ----------

    def _build_args(self) -> list[str]:
        args = []
        selected = [self.lang_listbox.get(i) for i in self.lang_listbox.curselection()]
        if not selected:
            selected = DEFAULT_LANGS
        args += ["-l", "+".join(selected)]

        mode = self.mode_var.get()
        if mode == "force-ocr":
            args.append("--force-ocr")
        elif mode == "redo-ocr":
            args.append("--redo-ocr")
        elif mode == "skip-text":
            args.append("--skip-text")
        # "strict" adds no flag

        if self.deskew_var.get():
            args.append("--deskew")
        if self.rotate_var.get():
            args.append("--rotate-pages")
        if self.clean_var.get():
            args.append("--clean")
        if self.clean_final_var.get():
            args.append("--clean-final")
        if self.remove_bg_var.get():
            args.append("--remove-background")

        args += ["--optimize", self.optimize_var.get()]
        args += ["--output-type", "pdfa" if self.pdfa_var.get() else "pdf"]

        extra = self.extra_args_var.get().strip()
        if extra:
            import shlex

            args += shlex.split(extra)

        return args

    def _resolve_output_path(self, input_path: Path) -> Path:
        if self.overwrite_var.get():
            return input_path
        out_dir = self.output_dir_var.get().strip()
        target_dir = Path(out_dir) if out_dir else input_path.parent
        suffix = self.suffix_var.get().strip()
        stem = input_path.stem + suffix if suffix else input_path.stem
        return target_dir / f"{stem}.pdf"

    # ---------- run / cancel ----------

    def start_run(self):
        if not self.input_files:
            messagebox.showwarning(APP_TITLE, "Add at least one PDF file first.")
            return
        if self.worker_thread and self.worker_thread.is_alive():
            return

        missing = [str(path) for path in self.input_files if not path.is_file()]
        if missing:
            messagebox.showerror(APP_TITLE, "These input files no longer exist:\n\n" + "\n".join(missing[:8]))
            return
        if self.overwrite_var.get() and not messagebox.askyesno(
            APP_TITLE,
            "Overwrite mode will replace the original PDF files. Continue?",
            icon="warning",
        ):
            return

        jobs = []
        seen_outputs: set[str] = set()
        try:
            common_args = self._build_args()
        except ValueError as exc:
            messagebox.showerror(APP_TITLE, f"Extra arguments are invalid:\n{exc}")
            return
        for input_path in self.input_files:
            output_path = self._resolve_output_path(input_path)
            if not self.overwrite_var.get() and output_path.resolve() == input_path.resolve():
                messagebox.showerror(APP_TITLE, "The output path would overwrite an input file. Choose a suffix or enable overwrite mode.")
                return
            if not self.overwrite_var.get() and output_path.exists():
                messagebox.showerror(APP_TITLE, f"An output file already exists:\n{output_path}\n\nChoose another folder or suffix.")
                return
            output_key = os.path.normcase(str(output_path.resolve()))
            if output_key in seen_outputs:
                messagebox.showerror(
                    APP_TITLE,
                    f"Two inputs would produce the same output file:\n{output_path}\n\n"
                    "Use separate batches or output folders.",
                )
                return
            seen_outputs.add(output_key)
            output_path.parent.mkdir(parents=True, exist_ok=True)
            jobs.append(OcrJob(input_path, output_path, common_args))

        self.cancel_requested = False
        self.run_btn.config(state="disabled")
        self.cancel_btn.config(state="normal")
        self.open_folder_btn.config(state="disabled")
        self._set_options_enabled(False)
        self.progress.config(maximum=len(jobs), value=0)
        self.notebook.select(2)

        self.worker_thread = threading.Thread(target=self._run_jobs, args=(jobs,), daemon=True)
        self.worker_thread.start()

    def cancel_run(self):
        self.cancel_requested = True
        self._kill_process_tree(self.current_process)
        self.log(">>> Cancel requested...")

    @staticmethod
    def _kill_process_tree(proc: "subprocess.Popen | None"):
        """Stop ocrmypdf and the tesseract/ghostscript children it spawned."""
        if proc is None or proc.poll() is not None:
            return
        if os.name == "nt":
            try:
                subprocess.run(
                    ["taskkill", "/PID", str(proc.pid), "/T", "/F"],
                    capture_output=True,
                    timeout=15,
                    creationflags=subprocess.CREATE_NO_WINDOW,
                )
                return
            except (OSError, subprocess.SubprocessError):
                pass
        proc.terminate()

    def _run_jobs(self, jobs: list[OcrJob]):
        total = len(jobs)
        success = 0
        failed = 0
        completed = 0
        for idx, job in enumerate(jobs, start=1):
            if self.cancel_requested:
                self.log(">>> Stopped before processing remaining files.")
                break

            self.log_queue.put(("status", (idx - 1, total, job.input_path.name)))
            # Write to a temporary file and move it into place only after OCRmyPDF succeeds,
            # so a failure or cancel never touches the original (matters in overwrite mode).
            temp_output = job.output_path.with_name(f".ocr-{uuid.uuid4().hex}.pdf")
            cmd = [self.ocrmypdf_exe, *job.args, str(job.input_path), str(temp_output)]
            self.log(f"\n=== [{idx}/{total}] {job.input_path.name} ===")
            self.log("$ " + " ".join(f'"{c}"' if " " in c else c for c in cmd))

            try:
                self.current_process = subprocess.Popen(
                    cmd,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    env={**os.environ, "PYTHONUTF8": "1"},
                    bufsize=1,
                    creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0,
                )
                assert self.current_process.stdout is not None
                for line in self.current_process.stdout:
                    self.log(line.rstrip())
                ret = self.current_process.wait()
                if ret == 0:
                    if not temp_output.is_file() or temp_output.stat().st_size == 0:
                        raise OSError("OCRmyPDF reported success but produced no output PDF.")
                    os.replace(temp_output, job.output_path)
            except FileNotFoundError:
                self.log(f"ERROR: could not find ocrmypdf executable at '{self.ocrmypdf_exe}'.")
                break
            except Exception as exc:  # noqa: BLE001
                self.log(f"ERROR: {exc}")
                failed += 1
                completed += 1
                continue
            finally:
                try:
                    temp_output.unlink(missing_ok=True)
                except OSError as exc:
                    self.log(f"Temporary file cleanup: {exc}")

            if ret == 0:
                self.log(f"OK -> {job.output_path}")
                self.last_output_dir = job.output_path.parent
                success += 1
            elif self.cancel_requested:
                self.log("Cancelled.")
                break
            else:
                self.log(f"FAILED (exit code {ret}) for {job.input_path.name}")
                failed += 1
            completed += 1

        self.current_process = None
        cancelled = total - completed
        self.log_queue.put(("finished", (success, failed, cancelled, total)))

    def _on_run_finished(self, result):
        success, failed, cancelled, total = result
        self.progress.config(value=success + failed)
        self.run_btn.config(state="normal")
        self.cancel_btn.config(state="disabled")
        self._set_options_enabled(True)
        if self.last_output_dir:
            self.open_folder_btn.config(state="normal")
        summary = f"Succeeded: {success}   Failed: {failed}   Cancelled: {cancelled}"
        self.status_var.set(summary)
        self.log("\n=== Batch complete ===\n" + summary)
        if failed:
            messagebox.showwarning(APP_TITLE, summary)
        else:
            messagebox.showinfo(APP_TITLE, summary)

    def open_output_folder(self):
        if self.last_output_dir and self.last_output_dir.exists():
            os.startfile(str(self.last_output_dir))  # noqa: S606

    def _set_options_enabled(self, enabled: bool):
        """Prevent settings from appearing to affect a batch already in progress."""
        widget_types = (ttk.Button, ttk.Checkbutton, ttk.Radiobutton, ttk.Entry, ttk.Combobox, tk.Listbox)

        def descendants(widget):
            for child in widget.winfo_children():
                yield child
                yield from descendants(child)

        if not enabled:
            self.saved_widget_states.clear()
            for widget in descendants(self.notebook):
                if isinstance(widget, widget_types):
                    try:
                        self.saved_widget_states[widget] = str(widget.cget("state"))
                        widget.config(state="disabled")
                    except tk.TclError:
                        pass
        else:
            for widget, state in self.saved_widget_states.items():
                try:
                    widget.config(state=state)
                except tk.TclError:
                    pass
            self.saved_widget_states.clear()
            self._update_output_state()

    def _load_settings(self):
        try:
            settings = json.loads(CONFIG_PATH.read_text(encoding="utf-8"))
        except (OSError, ValueError, TypeError):
            settings = {}
        self.output_dir_var.set(settings.get("output_dir", ""))
        self.suffix_var.set(settings.get("suffix", "_ocr"))
        self.mode_var.set(settings.get("mode", "skip-text"))
        for variable, key, default in (
            (self.deskew_var, "deskew", True),
            (self.rotate_var, "rotate", True),
            (self.clean_var, "clean", False),
            (self.clean_final_var, "clean_final", False),
            (self.remove_bg_var, "remove_background", False),
            (self.pdfa_var, "pdfa", True),
        ):
            variable.set(bool(settings.get(key, default)))
        self.optimize_var.set(str(settings.get("optimize", "1")))
        wanted = settings.get("languages", DEFAULT_LANGS)
        for index, language in enumerate(self.available_langs):
            if language in wanted:
                self.lang_listbox.selection_set(index)
        self._update_output_state()

    def _save_settings(self):
        settings = {
            "output_dir": self.output_dir_var.get(),
            "suffix": self.suffix_var.get(),
            "mode": self.mode_var.get(),
            "deskew": self.deskew_var.get(),
            "rotate": self.rotate_var.get(),
            "clean": self.clean_var.get(),
            "clean_final": self.clean_final_var.get(),
            "remove_background": self.remove_bg_var.get(),
            "pdfa": self.pdfa_var.get(),
            "optimize": self.optimize_var.get(),
            "languages": [self.lang_listbox.get(i) for i in self.lang_listbox.curselection()],
        }
        try:
            CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
            CONFIG_PATH.write_text(json.dumps(settings, indent=2), encoding="utf-8")
        except OSError:
            pass

    def on_close(self):
        if self.worker_thread and self.worker_thread.is_alive():
            if not messagebox.askyesno(APP_TITLE, "OCR is running. Cancel it and close?"):
                return
            self.cancel_run()
        self._save_settings()
        self.destroy()


def main():
    app = App()
    app.mainloop()


if __name__ == "__main__":
    main()
