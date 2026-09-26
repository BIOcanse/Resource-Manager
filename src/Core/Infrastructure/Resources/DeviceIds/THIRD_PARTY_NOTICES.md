# Device ID Database Notices

Resource Manager includes local snapshots of the USB ID Repository and PCI ID Repository. The files are parsed only when the device-topology details page is requested; the application does not query either service online at runtime.

## USB ID Repository

- File: `usb.ids`
- Version: `2026.06.26`
- Source: https://usb-ids.gowdy.us/
- Snapshot source: https://sources.debian.org/src/usb.ids/2026.06.26-1/
- SHA-256 (UTF-8, LF line endings): `271973050DC55EA7A558B1F693D41BEBE20BA6425C1B593BA144AA2EEF6824FC`
- Copyright: Copyright (C) Stephen J. Gowdy <linux.usb.ids@gmail.com>
- License choice used by Resource Manager: BSD 3-Clause

## PCI ID Repository

- File: `pci.ids`
- Version: `2026.07.09`
- Source: https://pci-ids.ucw.cz/
- SHA-256 (UTF-8, LF line endings): `50D1787D6DD5428ACDAE03DA4B211DCA77223F304A54CCF59980B1C9FE64DD7E`
- Copyright: Martin Mares and Albert Pool
- License choice used by Resource Manager: BSD 3-Clause

## BSD 3-Clause License

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

1. Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.
2. Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.
3. Neither the name of the copyright holder nor the names of its contributors may be used to endorse or promote products derived from this software without specific prior written permission.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
