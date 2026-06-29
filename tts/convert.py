import genie_tts as genie

genie.convert_to_onnx(
    torch_pth_path=r"E:\GPT-SoVITS-v2pro-20250604\SoVITS_weights_v2ProPlus\hutao_e8_s1072.pth",  # Replace with your .pth file
    torch_ckpt_path=r"E:\GPT-SoVITS-v2pro-20250604\GPT_weights_v2ProPlus\hutao-e15.ckpt",  # Replace with your .ckpt file
    output_dir=r"C:\Users\tjdgu\source\repos\GamerBio\tts\CharacterModels\v2ProPlus\hutao\tts_models"  # Directory to save ONNX model
)