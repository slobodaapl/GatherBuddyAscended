from pathlib import Path
import sys

import pyghidra


GHIDRA = Path("/opt/ghidra")
PROJECT_DIR = Path("/tmp/ffxivclientstructs-re.qTZ1Kx/ghidra-project")
PROJECT_NAME = "ffxiv-2026-08-10"
PROGRAM_PATH = "/ffxiv_dx11.exe"


if len(sys.argv) != 2:
    raise SystemExit("usage: xrefs.py ADDRESS")

pyghidra.start(install_dir=GHIDRA)

project = pyghidra.open_project(PROJECT_DIR, PROJECT_NAME)
try:
    with pyghidra.program_context(project, PROGRAM_PATH) as program:
        address = program.getAddressFactory().getDefaultAddressSpace().getAddress(
            int(sys.argv[1], 0)
        )
        functions = program.getFunctionManager()
        listing = program.getListing()
        for reference in program.getReferenceManager().getReferencesTo(address):
            source = reference.getFromAddress()
            function = functions.getFunctionContaining(source)
            instruction = listing.getInstructionAt(source)
            print(
                f"{source} {reference.getReferenceType()} "
                f"{function.getEntryPoint() if function else '-'} "
                f"{function.getName() if function else '<no-function>'} "
                f"{instruction if instruction else '<no-instruction>'}"
            )
finally:
    project.close()
