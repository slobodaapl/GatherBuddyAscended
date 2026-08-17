from pathlib import Path
import sys

import pyghidra


GHIDRA = Path("/opt/ghidra")
PROJECT_DIR = Path("/tmp/ffxivclientstructs-re.qTZ1Kx/ghidra-project")
PROJECT_NAME = "ffxiv-2026-08-10"
PROGRAM_PATH = "/ffxiv_dx11.exe"


if len(sys.argv) < 2:
    raise SystemExit("usage: decompile.py ADDRESS [ADDRESS ...]")

pyghidra.start(install_dir=GHIDRA)

from ghidra.app.decompiler import DecompInterface
from ghidra.util.task import ConsoleTaskMonitor


project = pyghidra.open_project(PROJECT_DIR, PROJECT_NAME)
try:
    with pyghidra.program_context(project, PROGRAM_PATH) as program:
        monitor = ConsoleTaskMonitor()
        decompiler = DecompInterface()
        decompiler.openProgram(program)
        address_space = program.getAddressFactory().getDefaultAddressSpace()
        function_manager = program.getFunctionManager()
        reference_manager = program.getReferenceManager()

        for raw_address in sys.argv[1:]:
            address = address_space.getAddress(int(raw_address, 0))
            function = function_manager.getFunctionContaining(address)
            if function is None:
                print(f"ADDRESS {raw_address}: no function")
                continue

            print(
                f"FUNCTION {function.getName()} "
                f"{function.getEntryPoint()}..{function.getBody().getMaxAddress()}"
            )
            for reference in reference_manager.getReferencesTo(function.getEntryPoint()):
                caller = function_manager.getFunctionContaining(reference.getFromAddress())
                caller_name = caller.getName() if caller is not None else "<no-function>"
                print(
                    f"XREF {reference.getFromAddress()} {reference.getReferenceType()} "
                    f"{caller_name}"
                )

            result = decompiler.decompileFunction(function, 120, monitor)
            if not result.decompileCompleted():
                print(f"DECOMPILE FAILED: {result.getErrorMessage()}")
                continue
            print(result.getDecompiledFunction().getC())
finally:
    project.close()
