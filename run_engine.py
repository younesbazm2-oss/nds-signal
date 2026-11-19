"""راه‌انداز موتور NDS — مسیر پروژه را مستقل از تنظیمات پایتون به sys.path اضافه می‌کند.

اجرا:
    cd nds-signal
    python run_engine.py
    python run_engine.py --config config/engine.yaml
"""
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from engine.main import main  # noqa: E402

if __name__ == "__main__":
    main()