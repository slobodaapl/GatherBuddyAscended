from pathlib import Path

import pyghidra


GHIDRA = Path("/opt/ghidra")
PROJECT_DIR = Path("/tmp/ffxivclientstructs-re.qTZ1Kx/ghidra-project")
PROJECT_NAME = "ffxiv-2026-08-10"
PROGRAM_PATH = "/ffxiv_dx11.exe"
RENAME_SCRIPT = Path(
    "/tmp/ffxivclientstructs-re.qTZ1Kx/FFXIVClientStructs-main/ida/ffxiv_idarename.py"
)


pyghidra.start(install_dir=GHIDRA)

from ghidra.util.task import ConsoleTaskMonitor


project = pyghidra.open_project(PROJECT_DIR, PROJECT_NAME)
try:
    with pyghidra.program_context(project, PROGRAM_PATH) as program:
        pyghidra.ghidra_script(RENAME_SCRIPT, project, program)
        program.save("Apply current FFXIVClientStructs names", ConsoleTaskMonitor())
finally:
    project.close()
