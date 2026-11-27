# Maintainer: Tom Cholin <tom.cholin@gmail.com>
#
# mono-hidpi-patch — Fix Mono WinForms HiDPI scaling on Unix
#
# Mono's libgdiplus renders text at 96 DPI regardless of Xft.dpi, but
# Font.Height and Graphics.DpiX DO respect it.  This mismatch breaks
# AutoScaleMode.Font, leaves menus/labels tiny, and causes layout
# overlaps on HiDPI displays.
#
# This package provides a Cecil-based patcher that fixes 6 methods
# across System.Drawing.dll and System.Windows.Forms.dll.
# Affects all Mono WinForms apps (KeePass, etc.) running under
# xwayland-satellite or any X server where Xft.dpi > 96.

pkgname=mono-hidpi-patch
pkgver=1.0.0
pkgrel=1
pkgdesc='Fix Mono WinForms HiDPI scaling on Unix (patches System.Drawing + System.Windows.Forms)'
url='https://github.com/tomcholin/mono-hidpi-patch'
arch=('any')
license=('MIT')
depends=('mono>=6.12')
makedepends=('mono')

source=(
    'patch_swf.cs'
    'apply-patch.sh'
    'keepass-hidpi'
    'LICENSE'
)
sha256sums=('SKIP' 'SKIP' 'SKIP' 'SKIP')

install=mono-hidpi-patch.install

build() {
    cd "$srcdir"
    mcs -r:/usr/lib/mono/gac/Mono.Cecil/0.11.1.0__0738eb9f132ed756/Mono.Cecil.dll \
        -r:System.Drawing \
        patch_swf.cs -out:patch_swf.exe
}

package() {
    cd "$srcdir"

    install -Dm 644 patch_swf.exe "$pkgdir/usr/lib/mono-hidpi-patch/patch_swf.exe"
    install -Dm 644 patch_swf.cs  "$pkgdir/usr/lib/mono-hidpi-patch/patch_swf.cs"
    install -Dm 755 apply-patch.sh "$pkgdir/usr/lib/mono-hidpi-patch/apply-patch.sh"
    install -Dm 755 keepass-hidpi "$pkgdir/usr/bin/keepass-hidpi"
    install -Dm 644 LICENSE "$pkgdir/usr/share/licenses/$pkgname/LICENSE"
}
