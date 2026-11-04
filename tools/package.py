"""Archive the self-contained installation and accompanying license notices."""
import argparse
from pathlib import Path
import shutil
import subprocess
import tarfile
import zipfile


def package(stage, target, dotnet):
    source = Path(__file__).resolve().parents[1]
    stage = stage.resolve()
    windows = target == "windows-x64"
    binary = stage / "bin" / ("tracebudget.exe" if windows else "tracebudget")
    assert subprocess.check_output([binary, "--version"], text=True).strip() == "tracebudget 0.1.0-preview"
    for name in ("README.md", "LICENSE", "docs/RELEASE.md", "assets/banner.svg", "examples/make_traces.py"):
        destination = stage / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source / name, destination)
    sdk = Path(subprocess.check_output([dotnet, "--list-sdks"], text=True)
               .splitlines()[-1].split("[", 1)[1].rstrip("]")).parent
    notices = stage / "licenses"
    notices.mkdir()
    for name in ("LICENSE.txt", "ThirdPartyNotices.txt"):
        shutil.copyfile(sdk / name, notices / ("DOTNET-" + name))

    output = source / "dist"
    output.mkdir(exist_ok=True)
    name = f"tracebudget-0.1.0-preview-{target}" + (".zip" if windows else ".tar.gz")
    entries = sorted(stage.rglob("*"))
    assert all(not path.is_symlink() and (path.is_dir() or path.is_file()) for path in entries)
    if windows:
        with zipfile.ZipFile(output / name, "x", compression=zipfile.ZIP_DEFLATED) as archive:
            for path in entries:
                if path.is_file():
                    archive.write(path, path.relative_to(stage).as_posix())
    else:
        with tarfile.open(output / name, "x:gz") as archive:
            for path in entries:
                if path.is_file():
                    archive.add(path, arcname=path.relative_to(stage).as_posix(), recursive=False)
    print(name)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("stage", type=Path)
    parser.add_argument("target", choices=("windows-x64", "linux-x64", "macos-arm64"))
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    package(args.stage, args.target, args.dotnet)
