/**
 * ComfyUI API handler for local image generation.
 * Builds workflows, submits prompts, polls for results, and fetches generated images.
 */

import { randomUUID } from "crypto";

interface ComfyUIImageRequest {
  serverUrl: string;
  prompt: string;
  referenceImage?: string; // base64 PNG
  onStatus?: (status: string) => void;
}

interface ComfyUIImageResponse {
  success: boolean;
  imageBase64?: string;
  description?: string;
  error?: string;
}

/**
 * Generate an image using ComfyUI's REST API.
 */
export async function generateImageComfyUI(
  request: ComfyUIImageRequest
): Promise<ComfyUIImageResponse> {
  const { serverUrl, prompt, referenceImage, onStatus } = request;
  const baseUrl = serverUrl.replace(/\/+$/, "");

  try {
    // Step 1: Check ComfyUI is reachable
    onStatus?.("🔗 Connecting to ComfyUI...");
    try {
      const healthCheck = await fetch(`${baseUrl}/system_stats`, {
        signal: AbortSignal.timeout(5000),
      });
      if (!healthCheck.ok) throw new Error(`HTTP ${healthCheck.status}`);
    } catch {
      return {
        success: false,
        error: `Cannot connect to ComfyUI at ${baseUrl}. Ensure ComfyUI is running.`,
      };
    }

    // Step 2: Find available checkpoint
    onStatus?.("📦 Finding checkpoint model...");
    const checkpoint = await findCheckpoint(baseUrl);
    if (!checkpoint) {
      return {
        success: false,
        error:
          "No checkpoint models found in ComfyUI. Please install a model (e.g., SDXL) to the models/checkpoints folder.",
      };
    }
    console.error(`[ComfyUI] Using checkpoint: ${checkpoint}`);

    // Step 3: Upload reference image if provided
    let uploadedImageName: string | undefined;
    if (referenceImage) {
      onStatus?.("📤 Uploading reference image...");
      uploadedImageName = (await uploadImage(baseUrl, referenceImage)) ?? undefined;
      if (!uploadedImageName) {
        console.error("[ComfyUI] Failed to upload reference image, proceeding with txt2img");
      }
    }

    // Step 4: Build workflow
    const clientId = randomUUID();
    const workflow =
      uploadedImageName
        ? buildImg2ImgWorkflow(checkpoint, prompt, uploadedImageName)
        : buildTxt2ImgWorkflow(checkpoint, prompt);

    // Step 5: Submit workflow
    onStatus?.("⚙️ Generating with ComfyUI...");
    const submitResponse = await fetch(`${baseUrl}/prompt`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ prompt: workflow, client_id: clientId }),
    });

    if (!submitResponse.ok) {
      const errorText = await submitResponse.text();
      return {
        success: false,
        error: `ComfyUI prompt submission failed: ${errorText.substring(0, 200)}`,
      };
    }

    const submitData = (await submitResponse.json()) as {
      prompt_id?: string;
      error?: string;
    };
    if (!submitData.prompt_id) {
      return { success: false, error: `ComfyUI error: ${submitData.error || "No prompt_id returned"}` };
    }

    const promptId = submitData.prompt_id;
    console.error(`[ComfyUI] Prompt submitted: ${promptId}`);

    // Step 6: Poll for completion
    const imageFilename = await pollForResult(baseUrl, promptId, onStatus);
    if (!imageFilename) {
      return { success: false, error: "ComfyUI generation timed out or failed." };
    }

    // Step 7: Fetch generated image
    onStatus?.("📥 Downloading generated image...");
    const imageBase64 = await fetchImage(baseUrl, imageFilename);
    if (!imageBase64) {
      return { success: false, error: "Failed to download generated image from ComfyUI." };
    }

    console.error("[ComfyUI] Image generated successfully.");
    return {
      success: true,
      imageBase64,
      description: `Generated with ComfyUI (${checkpoint})`,
    };
  } catch (err) {
    const msg = err instanceof Error ? err.message : String(err);
    console.error(`[ComfyUI] Error: ${msg}`);
    return { success: false, error: `ComfyUI error: ${msg}` };
  }
}

/**
 * Find the first available checkpoint model.
 */
async function findCheckpoint(baseUrl: string): Promise<string | null> {
  try {
    const response = await fetch(`${baseUrl}/object_info/CheckpointLoaderSimple`);
    if (!response.ok) return null;
    const data = (await response.json()) as Record<string, any>;
    const inputs = data?.CheckpointLoaderSimple?.input?.required?.ckpt_name;
    if (Array.isArray(inputs) && Array.isArray(inputs[0]) && inputs[0].length > 0) {
      return inputs[0][0];
    }
    return null;
  } catch {
    return null;
  }
}

/**
 * Upload a base64 PNG image to ComfyUI's input folder.
 */
async function uploadImage(
  baseUrl: string,
  base64Data: string
): Promise<string | null> {
  try {
    const imageBuffer = Buffer.from(base64Data, "base64");
    const filename = `unity_ref_${Date.now()}.png`;

    const formData = new FormData();
    formData.append("image", new Blob([imageBuffer], { type: "image/png" }), filename);
    formData.append("overwrite", "true");

    const response = await fetch(`${baseUrl}/upload/image`, {
      method: "POST",
      body: formData,
    });

    if (!response.ok) return null;
    const data = (await response.json()) as { name?: string };
    return data.name || filename;
  } catch {
    return null;
  }
}

/**
 * Build a txt2img workflow JSON for ComfyUI.
 */
function buildTxt2ImgWorkflow(
  checkpoint: string,
  prompt: string
): Record<string, any> {
  return {
    "4": {
      class_type: "CheckpointLoaderSimple",
      inputs: { ckpt_name: checkpoint },
    },
    "6": {
      class_type: "CLIPTextEncode",
      inputs: {
        text: prompt,
        clip: ["4", 1],
      },
    },
    "7": {
      class_type: "CLIPTextEncode",
      inputs: {
        text: "blurry, low quality, distorted, deformed, ugly, watermark",
        clip: ["4", 1],
      },
    },
    "5": {
      class_type: "EmptyLatentImage",
      inputs: { width: 1024, height: 1024, batch_size: 1 },
    },
    "3": {
      class_type: "KSampler",
      inputs: {
        seed: Math.floor(Math.random() * 2 ** 32),
        steps: 20,
        cfg: 7,
        sampler_name: "euler",
        scheduler: "normal",
        denoise: 1.0,
        model: ["4", 0],
        positive: ["6", 0],
        negative: ["7", 0],
        latent_image: ["5", 0],
      },
    },
    "8": {
      class_type: "VAEDecode",
      inputs: {
        samples: ["3", 0],
        vae: ["4", 2],
      },
    },
    "9": {
      class_type: "SaveImage",
      inputs: {
        filename_prefix: "UnityAgent",
        images: ["8", 0],
      },
    },
  };
}

/**
 * Build an img2img workflow JSON for ComfyUI.
 */
function buildImg2ImgWorkflow(
  checkpoint: string,
  prompt: string,
  inputImageName: string
): Record<string, any> {
  return {
    "4": {
      class_type: "CheckpointLoaderSimple",
      inputs: { ckpt_name: checkpoint },
    },
    "6": {
      class_type: "CLIPTextEncode",
      inputs: {
        text: prompt,
        clip: ["4", 1],
      },
    },
    "7": {
      class_type: "CLIPTextEncode",
      inputs: {
        text: "blurry, low quality, distorted, deformed, ugly, watermark",
        clip: ["4", 1],
      },
    },
    "10": {
      class_type: "LoadImage",
      inputs: { image: inputImageName },
    },
    "11": {
      class_type: "VAEEncode",
      inputs: {
        pixels: ["10", 0],
        vae: ["4", 2],
      },
    },
    "3": {
      class_type: "KSampler",
      inputs: {
        seed: Math.floor(Math.random() * 2 ** 32),
        steps: 20,
        cfg: 7,
        sampler_name: "euler",
        scheduler: "normal",
        denoise: 0.7,
        model: ["4", 0],
        positive: ["6", 0],
        negative: ["7", 0],
        latent_image: ["11", 0],
      },
    },
    "8": {
      class_type: "VAEDecode",
      inputs: {
        samples: ["3", 0],
        vae: ["4", 2],
      },
    },
    "9": {
      class_type: "SaveImage",
      inputs: {
        filename_prefix: "UnityAgent",
        images: ["8", 0],
      },
    },
  };
}

/**
 * Poll ComfyUI history for completion. Returns the output image filename.
 */
async function pollForResult(
  baseUrl: string,
  promptId: string,
  onStatus?: (status: string) => void
): Promise<string | null> {
  const maxWait = 120_000; // 120 seconds
  const pollInterval = 2_000; // 2 seconds
  const startTime = Date.now();

  while (Date.now() - startTime < maxWait) {
    await new Promise((r) => setTimeout(r, pollInterval));
    const elapsed = Math.round((Date.now() - startTime) / 1000);
    onStatus?.(`⚙️ ComfyUI generating... (${elapsed}s)`);

    try {
      const response = await fetch(`${baseUrl}/history/${promptId}`);
      if (!response.ok) continue;

      const data = (await response.json()) as Record<string, any>;
      const entry = data[promptId];
      if (!entry) continue;

      // Check for outputs
      const outputs = entry.outputs;
      if (outputs) {
        // Find SaveImage node output
        for (const nodeId of Object.keys(outputs)) {
          const nodeOutput = outputs[nodeId];
          if (nodeOutput.images && nodeOutput.images.length > 0) {
            const img = nodeOutput.images[0];
            return `${img.filename}|${img.subfolder || ""}|${img.type || "output"}`;
          }
        }
      }
    } catch {
      // Continue polling
    }
  }

  return null;
}

/**
 * Fetch an image from ComfyUI and return as base64.
 */
async function fetchImage(
  baseUrl: string,
  imageInfo: string
): Promise<string | null> {
  try {
    const [filename, subfolder, type] = imageInfo.split("|");
    const params = new URLSearchParams({
      filename,
      subfolder: subfolder || "",
      type: type || "output",
    });

    const response = await fetch(`${baseUrl}/view?${params}`);
    if (!response.ok) return null;

    const buffer = Buffer.from(await response.arrayBuffer());
    return buffer.toString("base64");
  } catch {
    return null;
  }
}
