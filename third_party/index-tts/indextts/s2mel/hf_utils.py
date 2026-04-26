import os


def load_custom_model_from_hf(repo_id, model_filename="pytorch_model.bin", config_filename="config.yml"):
    root = os.environ.get("INDEXTTS_MODEL_DIR") or os.getcwd()
    safe_repo = str(repo_id).replace("/", os.sep)
    base = os.path.join(root, safe_repo)
    model_path = os.path.join(base, model_filename)
    if not os.path.exists(model_path):
        raise FileNotFoundError("Local custom model file missing: " + model_path)
    if config_filename is None:
        return model_path
    config_path = os.path.join(base, config_filename)
    if not os.path.exists(config_path):
        raise FileNotFoundError("Local custom config file missing: " + config_path)
    return model_path, config_path
