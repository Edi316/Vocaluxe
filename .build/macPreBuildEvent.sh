#!/bin/sh
# Custom build: writes the version into Properties/AssemblyInfo.cs before the build (macOS, like the custom winPreBuild.ps1).
# Version: VOCALUXE_VERSION (the tag of the custom build workflow) or 0.0.<days since 2000-01-01>.<seconds since midnight / 2>
PROJECT=$1
ARCH=$2
cd "$PROJECT" || exit 1

# ever-increasing uint16 values (UTC), like winPreBuild.ps1
now=$(date -u +%s)
base=$(date -u -j -f '%Y-%m-%d %H:%M:%S' '2000-01-01 00:00:00' +%s)
versionDays=$(( (now - base) / 86400 ))
versionSeconds=$(( (now % 86400) / 2 ))
version="0.0.$versionDays.$versionSeconds"

if [ -n "$VOCALUXE_VERSION" ]; then
    version=$VOCALUXE_VERSION
fi

fullVersionName="Vocaluxe Custom Build $version ($ARCH)"

export VX_VERSION="$version" VX_TITLE="$fullVersionName" VX_YEAR="$(date +%Y)"
perl -CSD -pi -e '
    s/(AssemblyInformationalVersion\(")[^"]*(")/$1$ENV{VX_VERSION}$2/;
    s/((?:AssemblyVersion|AssemblyFileVersion)\(")[^"]*(")/$1$ENV{VX_VERSION}$2/;
    s/(AssemblyTitle\(")[^"]*(")/$1$ENV{VX_TITLE}$2/;
    s/(AssemblyCopyright\(".*?)[0-9]+(")/$1$ENV{VX_YEAR}$2/;
' Properties/AssemblyInfo.cs
