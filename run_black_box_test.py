"""Safe diagnostic self-test: deliberately raises without touching ZervDiag DB."""

import sys

from PySide6.QtCore import QCoreApplication

from diagnostics.black_box import get_app_logger, install_black_box


def main():
    app = QCoreApplication(sys.argv)
    install_black_box(app)

    get_app_logger().info("BLACK BOX SELF-TEST | intentional exception follows")

    raise RuntimeError(
        "ZERV_DIAG_BLACK_BOX_TEST: intentional test exception"
    )


if __name__ == "__main__":
    main()
