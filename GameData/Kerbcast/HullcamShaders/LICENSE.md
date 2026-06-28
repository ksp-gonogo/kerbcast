# HullcamShaders — license notice

`shaders.linux` in this directory is **not** part of kerbcast's own code and is
**not** covered by kerbcast's CC BY-NC-SA 4.0 license.

It is a Unity asset bundle built from the shader sources of
[HullcamVDSContinued](https://github.com/linuxgurugamer/HullcamVDSContinued)
(`HullCameraAssets`), which is licensed under the **GNU General Public License
v3.0**. The compiled bundle is therefore a derivative work of that project and
is distributed under the **GPL-3.0** on the same terms.

kerbcast ships it only as a Linux/Proton compatibility shim (the stock Hullcam
shaders do not load there) and otherwise relies on a user-installed copy of
HullcamVDS at runtime. kerbcast's plugin and sidecar do not link this bundle;
it travels here as a separate, independently licensed work (GPL-3.0 "mere
aggregation").

- Upstream source: https://github.com/linuxgurugamer/HullcamVDSContinued
- License text: https://www.gnu.org/licenses/gpl-3.0.txt
