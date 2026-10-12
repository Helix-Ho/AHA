# Run in Rhino with RunPythonScript.
import os, runpy
root = os.path.abspath(os.path.join(os.path.dirname(__file__), '../../..'))
runpy.run_path(os.path.join(root, 'src/aha/checks/run_rhino.py'), run_name='aha_host_checks')['main']('eum-crawl')
