# Project-provided binary package: repackages the self-contained release tarball.
%global debug_package %{nil}
%global __strip /bin/true
%global __brp_strip %{nil}
%global __brp_strip_static_archive %{nil}
%global __brp_strip_comment_note %{nil}
%global __brp_mangle_shebangs %{nil}
# The bundled .NET runtime's own libraries are private: neither provided to nor required from the system.
%global __provides_exclude_from ^%{_libdir}/bonfire/.*$
%global __requires_exclude ^(libmscordaccore\\.so|libclrjit\\.so|libcoreclr\\.so|libhostfxr\\.so|libhostpolicy\\.so).*$

Name:           bonfire
Version:        %{bonfire_version}
Release:        %{bonfire_release}%{?dist}
Summary:        Run Minecraft servers safely in Docker
License:        GPL-3.0-or-later AND MIT AND Apache-2.0 AND OFL-1.1
URL:            https://github.com/JamJan05/bonfire
Source0:        bonfire-%{version}-linux-x64.tar.gz
ExclusiveArch:  x86_64

# Loaded at run time with dlopen, so not found by the automatic dependency generator.
Requires:       libicu
Requires:       openssl-libs
Requires:       ca-certificates
Requires:       fontconfig
Requires:       libX11
Requires:       libXext
Requires:       libXfixes
Requires:       libXi
Requires:       libXrandr
Requires:       libXcursor
Requires:       libICE
Requires:       libSM
Recommends:     libglvnd-glx
Recommends:     libglvnd-egl
Recommends:     dbus-libs
Recommends:     (xdg-desktop-portal or gtk3)
Suggests:       (moby-engine or docker-ce)

%description
Bonfire creates and manages Minecraft Java servers (Vanilla, Fabric, Paper) in
hardened Docker containers: logs, console, players, settings, backups, and mods
or plugins from Modrinth with dependency checks, updates and undo.

Needs a rootful Docker daemon on this machine; the user must be allowed to use
its socket (membership in the "docker" group is root-equivalent). This is a
project-provided package, not an official Fedora package.

%prep
%setup -q -n bonfire-%{version}

%build
# Nothing to build: the payload is the self-contained release build.

%install
mkdir -p %{buildroot}%{_libdir} %{buildroot}%{_bindir} %{buildroot}%{_datadir}
cp -a lib %{buildroot}%{_libdir}/bonfire
ln -s ../%{_lib}/bonfire/bonfire %{buildroot}%{_bindir}/bonfire
cp -a share/applications share/metainfo share/icons %{buildroot}%{_datadir}/

%files
%license share/doc/bonfire/LICENSE share/doc/bonfire/THIRD-PARTY-NOTICES.txt
%doc share/doc/bonfire/README.md
%{_libdir}/bonfire/
%{_bindir}/bonfire
%{_datadir}/applications/io.github.jamjan05.Bonfire.desktop
%{_datadir}/metainfo/io.github.jamjan05.Bonfire.metainfo.xml
%{_datadir}/icons/hicolor/*/apps/io.github.jamjan05.Bonfire.*
