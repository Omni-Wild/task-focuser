pip install -r requirements.txt -r requirements-dev.txt
pyinstaller --noconfirm --onefile --windowed --name "Task Focuser" --icon NONE -p . task_focuser/__main__.py
