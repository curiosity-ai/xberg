//! Vendored from candle-transformers 0.10.2 GLM-4 decoder.
//!
//! Thin in-tree fork exposing `forward_embeds()` — the path the upstream
//! model does not provide. The upstream `Model::forward(input_ids)` only
//! accepts token IDs (embedding lookup is private). GLM-OCR concatenates
//! vision-token embeddings with text-token embeddings, then feeds the
//! combined `(B, seq, hidden)` tensor straight to the transformer stack.
//!
//! This fork exposes:
//! - `forward()` — standard token-id path (upstream `Model::forward` surface)
//! - `forward_embeds()` — embedding-level path (NEW, skips embedding lookup)
//! - `embed_tokens()` — exposes embedding layer for text-token embedding
//! - `clear_kv_cache()` — resets KV cache between calls
//!
//! Vendored config, embedding layer, RoPE, attention block (with KV cache),
//! MLP block, decoder layer, and model wrapper. Does not vendor weight
//! quantization, chat templates, or tokenization.
//!
//! Upstream source: candle-transformers/src/models/glm4.rs (commit hash TBD).

use serde::{Deserialize, Serialize};

/// Nested rotary-embedding parameters. Upstream `config.json` stores
/// `rope_theta` and `mrope_section` inside `text_config.rope_parameters`.
#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct RopeParameters {
    #[serde(default = "default_rope_theta")]
    pub rope_theta: f64,
    #[serde(default = "default_mrope_section")]
    pub mrope_section: Vec<usize>,
}

fn default_rope_theta() -> f64 {
    10_000.0
}

fn default_mrope_section() -> Vec<usize> {
    vec![16, 24, 24]
}

impl Default for RopeParameters {
    fn default() -> Self {
        Self {
            rope_theta: default_rope_theta(),
            mrope_section: default_mrope_section(),
        }
    }
}

#[derive(Debug, Clone, Deserialize, Serialize)]
pub struct DecoderConfig {
    pub hidden_size: usize,
    pub num_attention_heads: usize,
    pub num_key_value_heads: usize,
    pub num_hidden_layers: usize,
    pub intermediate_size: usize,
    pub vocab_size: usize,
    pub max_position_embeddings: usize,
    pub rms_norm_eps: f64,
    pub tie_word_embeddings: bool,
    /// Explicit per-head dimension. Upstream GLM-OCR sets `head_dim = 128`
    /// independently of `hidden_size / num_attention_heads` (1536 / 16 = 96 ≠ 128).
    /// `0` falls back to the derived value for compatibility with vanilla GLM-4.
    #[serde(default = "default_head_dim")]
    pub head_dim: usize,
    /// Number of next-N-token-prediction layers stacked after the main decoder.
    /// GLM-OCR ships with 1; current decoder ignores it (vanilla autoregressive
    /// generation works without it but the model's MTP head is unused).
    #[serde(default = "default_num_nextn_predict_layers")]
    pub num_nextn_predict_layers: usize,
    /// Nested RoPE parameters (matches upstream `text_config.rope_parameters`).
    /// Contains `rope_theta` and `mrope_section`.
    #[serde(default)]
    pub rope_parameters: RopeParameters,
    /// Whether QKV projections include a bias. Upstream GLM-OCR uses `false`.
    #[serde(default)]
    pub attention_bias: bool,
}

fn default_head_dim() -> usize {
    128
}

fn default_num_nextn_predict_layers() -> usize {
    1
}

impl DecoderConfig {
    /// Convenience accessor matching the previous flat `rope_theta` field.
    pub fn rope_theta(&self) -> f64 {
        self.rope_parameters.rope_theta
    }

    /// Convenience accessor matching the previous flat `mrope_section` field.
    pub fn mrope_section(&self) -> &[usize] {
        &self.rope_parameters.mrope_section
    }
}

impl Default for DecoderConfig {
    fn default() -> Self {
        Self {
            hidden_size: 1536,
            num_attention_heads: 16,
            num_key_value_heads: 8,
            num_hidden_layers: 16,
            intermediate_size: 4608,
            vocab_size: 59392,
            max_position_embeddings: 131_072,
            rms_norm_eps: 1e-5,
            tie_word_embeddings: false,
            head_dim: default_head_dim(),
            num_nextn_predict_layers: default_num_nextn_predict_layers(),
            rope_parameters: RopeParameters::default(),
            attention_bias: false,
        }
    }
}

#[cfg(not(target_arch = "wasm32"))]
mod imp {
    use candle_core::{D, DType, Device, IndexOp, Tensor};
    use candle_nn::VarBuilder;

    use super::DecoderConfig;
    use crate::CandleOcrError;
    use crate::error::Result;

    /// Precomputed RoPE (Rotary Positional Embedding) cosines and sines.
    /// Supports both standard 1-D RoPE and multimodal RoPE (M-RoPE) with separate
    /// positional embeddings for temporal, height, and width axes.
    #[derive(Debug, Clone)]
    struct RotaryEmbedding {
        cos: Tensor,
        sin: Tensor,
        mrope_sections: Vec<usize>,
        mrope_cos: Option<(Tensor, Tensor, Tensor)>,
        mrope_sin: Option<(Tensor, Tensor, Tensor)>,
    }

    impl RotaryEmbedding {
        /// Create a new RoPE embedding, optionally with multimodal sections.
        ///
        /// If `mrope_sections` is empty, uses standard 1-D RoPE (dim dims total).
        /// If `mrope_sections` is non-empty (e.g. [16, 24, 24]), creates separate
        /// cos/sin caches for each axis, each with dim = section_size / 2 pairs.
        fn new(
            max_seq_len: usize,
            dim: usize,
            rope_theta: f64,
            mrope_sections: Vec<usize>,
            dtype: DType,
            dev: &Device,
        ) -> Result<Self> {
            if !mrope_sections.is_empty() {
                let total: usize = mrope_sections.iter().map(|s| s * 2).sum();
                if total != dim {
                    return Err(CandleOcrError::InferenceFailed(format!(
                        "M-RoPE sections sum (each × 2) must equal head_dim. Got sections {:?} (sum={}) but head_dim={}",
                        mrope_sections, total, dim
                    )));
                }
            }
            if mrope_sections.is_empty() {
                let inv_freq: Vec<f32> = (0..dim)
                    .step_by(2)
                    .map(|i| 1f32 / rope_theta.powf(i as f64 / dim as f64) as f32)
                    .collect();
                let inv_freq_len = inv_freq.len();
                let inv_freq = Tensor::from_vec(inv_freq, (inv_freq_len,), dev)?.to_dtype(dtype)?;
                let t = Tensor::arange(0u32, max_seq_len as u32, dev)?
                    .to_dtype(dtype)?
                    .reshape((max_seq_len, 1))?;
                let freqs = t.matmul(&inv_freq.reshape((1, inv_freq_len))?)?;

                let cos = freqs.cos()?;
                let sin = freqs.sin()?;

                Ok(Self {
                    cos,
                    sin,
                    mrope_sections,
                    mrope_cos: None,
                    mrope_sin: None,
                })
            } else {
                let inv_freq: Vec<f32> = (0..dim)
                    .step_by(2)
                    .map(|i| 1f32 / rope_theta.powf(i as f64 / dim as f64) as f32)
                    .collect();
                let inv_freq_len = inv_freq.len();
                let inv_freq = Tensor::from_vec(inv_freq, (inv_freq_len,), dev)?.to_dtype(dtype)?;
                let t = Tensor::arange(0u32, max_seq_len as u32, dev)?
                    .to_dtype(dtype)?
                    .reshape((max_seq_len, 1))?;
                let freqs_half = t.matmul(&inv_freq.reshape((1, inv_freq_len))?)?;
                let freqs_full = Tensor::cat(&[&freqs_half, &freqs_half], D::Minus1)?;
                let cos_full = freqs_full.cos()?;
                let sin_full = freqs_full.sin()?;

                let mrope_cos = (cos_full.clone(), cos_full.clone(), cos_full.clone());
                let mrope_sin = (sin_full.clone(), sin_full.clone(), sin_full.clone());

                Ok(Self {
                    cos: cos_full,
                    sin: sin_full,
                    mrope_sections: mrope_sections.clone(),
                    mrope_cos: Some(mrope_cos),
                    mrope_sin: Some(mrope_sin),
                })
            }
        }

        /// Apply M-RoPE with explicit position_ids: shape (3, B, seq_len) or (B, seq_len) for fallback
        fn apply_multimodal(&self, xs: &Tensor, position_ids: &Tensor) -> Result<Tensor> {
            let (_seq_len, _b, _n_heads, _head_dim) = xs.dims4()?;
            let seq_len = xs.dim(0)?;
            let head_dim = xs.dim(D::Minus1)?;

            if !self.mrope_sections.is_empty() && position_ids.dim(0)? == 3 {
                return self.apply_mrope_multimodal(xs, position_ids, head_dim, seq_len);
            }

            let offset = if position_ids.dims().len() == 2 {
                position_ids.i((0, 0))?.to_scalar::<u32>()? as usize
            } else {
                0
            };
            self.apply_standard_rope(xs, head_dim, seq_len, offset)
        }

        /// Standard 1-D RoPE (fallback path) using the GPT-NeoX split-halves form.
        ///
        /// Upstream `transformers.models.glm4.apply_rotary_pos_emb` rotates the
        /// full `head_dim` via `rotate_half(x) = cat([-x2, x1], -1)` and:
        ///
        /// ```text
        /// out = (x * cos_full) + (rotate_half(x) * sin_full)
        /// ```
        ///
        /// where `cos_full = cat([cos_half, cos_half], -1)` (and likewise for
        /// `sin_full`). The cached `self.cos` / `self.sin` are `(max_seq, head_dim/2)`;
        /// we tile them along the last dim to span the full head and broadcast
        /// against `(seq, B, H, head_dim)`.
        fn apply_standard_rope(
            &self,
            xs: &Tensor,
            head_dim: usize,
            seq_len: usize,
            seqlen_offset: usize,
        ) -> Result<Tensor> {
            let cos_half = self.cos.narrow(0, seqlen_offset, seq_len)?;
            let sin_half = self.sin.narrow(0, seqlen_offset, seq_len)?;

            let cos_full = Tensor::cat(&[&cos_half, &cos_half], D::Minus1)?;
            let sin_full = Tensor::cat(&[&sin_half, &sin_half], D::Minus1)?;

            let cos_bcast = cos_full.unsqueeze(1)?.unsqueeze(2)?;
            let sin_bcast = sin_full.unsqueeze(1)?.unsqueeze(2)?;

            if !head_dim.is_multiple_of(2) {
                return Err(crate::error::CandleOcrError::Candle(candle_core::Error::Msg(format!(
                    "head_dim must be even for rotate_half; got {head_dim}"
                ))));
            }
            let half = head_dim / 2;
            let x1 = xs.narrow(D::Minus1, 0, half)?;
            let x2 = xs.narrow(D::Minus1, half, head_dim - half)?;
            let neg_x2 = x2.neg()?;
            let rotated = Tensor::cat(&[&neg_x2, &x1], D::Minus1)?;

            let _ = seq_len;
            let term0 = xs.broadcast_mul(&cos_bcast)?;
            let term1 = rotated.broadcast_mul(&sin_bcast)?;
            Ok((term0 + term1)?)
        }

        /// Multimodal RoPE: stitch per-axis cos/sin via position_ids, then apply
        /// the GPT-NeoX split-halves `rotate_half` form.
        ///
        /// Matches upstream `transformers.models.glm4v.modeling_glm4v
        /// .apply_multimodal_rotary_pos_emb`.
        ///
        /// Each axis table has shape `(max_seq, head_dim)`.  For each axis we
        /// gather the rows named by `position_ids[axis]` via `index_select`,
        /// slice the section that axis owns, then concatenate across axes to form
        /// the final `(B, seq, head_dim)` cos/sin.  The rotation is:
        ///
        /// ```text
        /// rotate_half(x) = cat([-x[..., d/2:], x[..., :d/2]], -1)
        /// out = x * cos + rotate_half(x) * sin
        /// ```
        ///
        /// `xs` shape on entry: `(seq, B, H, head_dim)`.
        /// `position_ids` shape: `(3, B, seq)`.
        fn apply_mrope_multimodal(
            &self,
            xs: &Tensor,
            position_ids: &Tensor,
            head_dim: usize,
            _seq_len: usize,
        ) -> Result<Tensor> {
            let (Some((t_cos, h_cos, w_cos)), Some((t_sin, h_sin, w_sin))) = (&self.mrope_cos, &self.mrope_sin) else {
                return Err(CandleOcrError::InferenceFailed(
                    "M-RoPE configured but cos/sin tables not initialized".to_string(),
                ));
            };

            let batch_size = position_ids.dim(1)?;
            let seq_len = position_ids.dim(2)?;

            let t_pos = position_ids.i((0, .., ..))?;
            let h_pos = position_ids.i((1, .., ..))?;
            let w_pos = position_ids.i((2, .., ..))?;

            let t_flat = t_pos.flatten_all()?;
            let h_flat = h_pos.flatten_all()?;
            let w_flat = w_pos.flatten_all()?;

            let to_u32 = |t: Tensor| -> Result<Tensor> {
                if t.dtype() == DType::U32 {
                    Ok(t)
                } else {
                    Ok(t.to_dtype(DType::U32)?)
                }
            };
            let t_flat = to_u32(t_flat)?;
            let h_flat = to_u32(h_flat)?;
            let w_flat = to_u32(w_flat)?;

            let gather = |table: &Tensor, flat_idx: &Tensor| -> Result<Tensor> {
                let gathered = table.index_select(flat_idx, 0)?;
                Ok(gathered.reshape((batch_size, seq_len, head_dim))?)
            };

            let t_cos_at = gather(t_cos, &t_flat)?;
            let h_cos_at = gather(h_cos, &h_flat)?;
            let w_cos_at = gather(w_cos, &w_flat)?;
            let t_sin_at = gather(t_sin, &t_flat)?;
            let h_sin_at = gather(h_sin, &h_flat)?;
            let w_sin_at = gather(w_sin, &w_flat)?;

            let sec0 = self.mrope_sections[0] * 2;
            let sec1 = self.mrope_sections[1] * 2;
            let sec2 = head_dim - sec0 - sec1;

            let stitch = |t_at: &Tensor, h_at: &Tensor, w_at: &Tensor| -> Result<Tensor> {
                let t_slice = t_at.narrow(D::Minus1, 0, sec0)?;
                let h_slice = h_at.narrow(D::Minus1, sec0, sec1)?;
                let w_slice = w_at.narrow(D::Minus1, sec0 + sec1, sec2)?;
                Ok(Tensor::cat(&[&t_slice, &h_slice, &w_slice], D::Minus1)?)
            };

            let final_cos = stitch(&t_cos_at, &h_cos_at, &w_cos_at)?;
            let final_sin = stitch(&t_sin_at, &h_sin_at, &w_sin_at)?;

            let cos_bcast = final_cos.permute([1, 0, 2])?.unsqueeze(2)?;
            let sin_bcast = final_sin.permute([1, 0, 2])?.unsqueeze(2)?;

            let half = head_dim / 2;
            let x1 = xs.narrow(D::Minus1, 0, half)?;
            let x2 = xs.narrow(D::Minus1, half, head_dim - half)?;
            let rotated = Tensor::cat(&[&x2.neg()?, &x1], D::Minus1)?;

            let term0 = xs.broadcast_mul(&cos_bcast)?;
            let term1 = rotated.broadcast_mul(&sin_bcast)?;
            Ok((term0 + term1)?)
        }
    }

    /// Build the additive causal mask for attention scores of shape
    /// `(B*H, q_len, k_len)`. Returns a `(q_len, k_len)` tensor of `0` where
    /// key position `j <= kv_offset + i` (allowed) and `-inf` otherwise.
    ///
    /// `kv_offset` is the count of cached keys before this forward call —
    /// during prefill it is `0`; during autoregressive decoding it grows by
    /// one per step.
    fn causal_mask(q_len: usize, k_len: usize, kv_offset: usize, dev: &Device, dtype: DType) -> Result<Tensor> {
        let neg_inf = f32::NEG_INFINITY;
        let mut data = Vec::with_capacity(q_len * k_len);
        for i in 0..q_len {
            let allowed = kv_offset + i;
            for j in 0..k_len {
                data.push(if j <= allowed { 0.0_f32 } else { neg_inf });
            }
        }
        let mask = Tensor::from_vec(data, (q_len, k_len), dev)?;
        Ok(mask.to_dtype(dtype)?)
    }

    /// Helper to build a default position_ids tensor for standard 1-D inference.
    /// Shape: (1, B, seq_len) where all position values are the sequence indices.
    /// Used when M-RoPE is not active; engine can override with explicit 3-row positions.
    fn default_position_ids(batch_size: usize, seq_len: usize, seqlen_offset: usize, dev: &Device) -> Result<Tensor> {
        let positions: Vec<u32> = (seqlen_offset..seqlen_offset + seq_len).map(|i| i as u32).collect();
        let pos_tensor = Tensor::from_vec(positions, (seq_len,), dev)?;
        let pos_broadcast = pos_tensor.unsqueeze(0)?.broadcast_as((batch_size, seq_len))?;
        Ok(pos_broadcast.unsqueeze(0)?)
    }

    /// Multi-query attention block with KV cache.
    #[derive(Debug, Clone)]
    struct Attention {
        q_proj: candle_nn::Linear,
        k_proj: candle_nn::Linear,
        v_proj: candle_nn::Linear,
        out_proj: candle_nn::Linear,
        num_heads: usize,
        num_kv_heads: usize,
        head_dim: usize,
        scale: f32,
        kv_cache: Option<(Tensor, Tensor)>,
    }

    impl Attention {
        fn new(config: &DecoderConfig, vb: VarBuilder) -> Result<Self> {
            let head_dim = if config.head_dim > 0 {
                config.head_dim
            } else {
                config.hidden_size / config.num_attention_heads
            };
            let scale = 1.0 / (head_dim as f32).sqrt();

            let q_size = config.num_attention_heads * head_dim;
            let kv_size = config.num_key_value_heads * head_dim;

            let q_proj = if config.attention_bias {
                candle_nn::linear(config.hidden_size, q_size, vb.pp("q_proj"))?
            } else {
                candle_nn::linear_no_bias(config.hidden_size, q_size, vb.pp("q_proj"))?
            };

            let k_proj = if config.attention_bias {
                candle_nn::linear(config.hidden_size, kv_size, vb.pp("k_proj"))?
            } else {
                candle_nn::linear_no_bias(config.hidden_size, kv_size, vb.pp("k_proj"))?
            };

            let v_proj = if config.attention_bias {
                candle_nn::linear(config.hidden_size, kv_size, vb.pp("v_proj"))?
            } else {
                candle_nn::linear_no_bias(config.hidden_size, kv_size, vb.pp("v_proj"))?
            };

            let out_proj = candle_nn::linear_no_bias(q_size, config.hidden_size, vb.pp("o_proj"))?;

            Ok(Self {
                q_proj,
                k_proj,
                v_proj,
                out_proj,
                num_heads: config.num_attention_heads,
                num_kv_heads: config.num_key_value_heads,
                head_dim,
                scale,
                kv_cache: None,
            })
        }

        fn reset_kv_cache(&mut self) {
            self.kv_cache = None;
        }

        /// Forward with explicit position_ids for multimodal RoPE support.
        /// position_ids shape: (1 or 3, B, seq_len)
        ///   - If shape is (1, B, seq), standard 1-D positions (fallback)
        ///   - If shape is (3, B, seq), multimodal positions (t, h, w)
        fn forward_with_position_ids(
            &mut self,
            xs: &Tensor,
            rope: &RotaryEmbedding,
            position_ids: &Tensor,
        ) -> Result<Tensor> {
            let (batch_size, seq_len, _) = xs.dims3()?;

            let q = xs.apply(&self.q_proj)?;
            let k = xs.apply(&self.k_proj)?;
            let v = xs.apply(&self.v_proj)?;

            let q_size = self.num_heads * self.head_dim;

            let q = q.reshape((batch_size, seq_len, self.num_heads, self.head_dim))?;
            let k = k.reshape((batch_size, seq_len, self.num_kv_heads, self.head_dim))?;
            let v = v.reshape((batch_size, seq_len, self.num_kv_heads, self.head_dim))?;

            let q = self.apply_rope(&q, rope, position_ids)?;
            let k = self.apply_rope(&k, rope, position_ids)?;

            let (k, v) = if let Some((prev_k, prev_v)) = &self.kv_cache {
                (Tensor::cat(&[prev_k, &k], 1)?, Tensor::cat(&[prev_v, &v], 1)?)
            } else {
                (k, v)
            };
            self.kv_cache = Some((k.clone(), v.clone()));

            let repeat_ratio = self.num_heads / self.num_kv_heads;
            let k_expanded = k
                .unsqueeze(3)?
                .broadcast_as((batch_size, k.dim(1)?, self.num_kv_heads, repeat_ratio, self.head_dim))?
                .reshape((batch_size, k.dim(1)?, self.num_heads, self.head_dim))?;
            let v_expanded = v
                .unsqueeze(3)?
                .broadcast_as((batch_size, v.dim(1)?, self.num_kv_heads, repeat_ratio, self.head_dim))?
                .reshape((batch_size, v.dim(1)?, self.num_heads, self.head_dim))?;

            let q_reshaped = q
                .permute([0, 2, 1, 3])?
                .reshape((batch_size * self.num_heads, seq_len, self.head_dim))?;
            let k_reshaped = k_expanded.permute([0, 2, 1, 3])?.reshape((
                batch_size * self.num_heads,
                k_expanded.dim(1)?,
                self.head_dim,
            ))?;
            let v_reshaped = v_expanded.permute([0, 2, 1, 3])?.reshape((
                batch_size * self.num_heads,
                v_expanded.dim(1)?,
                self.head_dim,
            ))?;

            let scores = q_reshaped.matmul(&k_reshaped.transpose(1, 2)?)?;
            let scores = (scores * (self.scale as f64))?;

            let k_len = k_reshaped.dim(1)?;
            let kv_offset = k_len - seq_len;
            let scores_dtype = scores.dtype();
            let mask = causal_mask(seq_len, k_len, kv_offset, scores.device(), scores_dtype)?;
            let scores = scores.broadcast_add(&mask)?;

            let attn_weights = candle_nn::ops::softmax_last_dim(&scores)?;
            let context = attn_weights.matmul(&v_reshaped)?;

            let context = context.reshape((batch_size, self.num_heads, seq_len, self.head_dim))?;
            let context = context.permute([0, 2, 1, 3])?;
            let context = context.reshape((batch_size, seq_len, q_size))?;

            Ok(context.apply(&self.out_proj)?)
        }

        /// Backward-compat forward with seqlen_offset (used for text-only paths).
        /// Internally converts to position_ids and calls forward_with_position_ids.
        #[allow(dead_code)]
        fn forward(&mut self, xs: &Tensor, rope: &RotaryEmbedding, seqlen_offset: usize) -> Result<Tensor> {
            let (batch_size, seq_len, _) = xs.dims3()?;
            let dev = xs.device();

            let position_ids = default_position_ids(batch_size, seq_len, seqlen_offset, dev)?;

            self.forward_with_position_ids(xs, rope, &position_ids)
        }

        fn apply_rope(&self, xs: &Tensor, rope: &RotaryEmbedding, position_ids: &Tensor) -> Result<Tensor> {
            let (_batch_size, _seq_len, _num_heads, _head_dim) = xs.dims4()?;

            let xs_perm = xs
                .permute([1, 0, 2, 3])
                .map_err(|e| CandleOcrError::InferenceFailed(format!("Permute for RoPE: {}", e)))?;

            let rotated = rope.apply_multimodal(&xs_perm, position_ids)?;

            rotated
                .permute([1, 0, 2, 3])
                .map_err(|e| CandleOcrError::InferenceFailed(format!("Permute after RoPE: {}", e)))
        }
    }

    /// MLP block (SwiGLU activation) with fused gate+up projection.
    ///
    /// GLM-OCR ships with a single fused `gate_up_proj` linear of shape
    /// `(2 * intermediate_size, hidden_size)` instead of the separate
    /// `gate_proj` + `up_proj` pair used by vanilla GLM-4. The two halves are
    /// split along the last dim during forward.
    #[derive(Debug, Clone)]
    struct Mlp {
        gate_up_proj: candle_nn::Linear,
        down_proj: candle_nn::Linear,
        intermediate_size: usize,
    }

    impl Mlp {
        fn new(config: &DecoderConfig, vb: VarBuilder) -> Result<Self> {
            let gate_up_proj =
                candle_nn::linear_no_bias(config.hidden_size, 2 * config.intermediate_size, vb.pp("gate_up_proj"))?;
            let down_proj =
                candle_nn::linear_no_bias(config.intermediate_size, config.hidden_size, vb.pp("down_proj"))?;

            Ok(Self {
                gate_up_proj,
                down_proj,
                intermediate_size: config.intermediate_size,
            })
        }

        fn forward(&self, xs: &Tensor) -> Result<Tensor> {
            let gate_up = xs.apply(&self.gate_up_proj)?;
            let gate = gate_up.narrow(D::Minus1, 0, self.intermediate_size)?;
            let up = gate_up.narrow(D::Minus1, self.intermediate_size, self.intermediate_size)?;
            let hidden = (gate.silu()? * up)?;
            Ok(hidden.apply(&self.down_proj)?)
        }
    }

    /// RMSNorm normalization.
    #[derive(Debug, Clone)]
    struct RmsNorm {
        weight: Tensor,
        eps: f64,
    }

    impl RmsNorm {
        fn new(size: usize, eps: f64, vb: VarBuilder) -> Result<Self> {
            let weight = vb.get(size, "weight")?;
            Ok(Self { weight, eps })
        }

        fn forward(&self, xs: &Tensor) -> Result<Tensor> {
            let norm_sq = xs.sqr()?.mean_keepdim(D::Minus1)?;
            let norm = (norm_sq + self.eps)?.sqrt()?;
            let normalized = xs.broadcast_div(&norm)?;
            Ok(normalized.broadcast_mul(&self.weight)?)
        }
    }

    /// Decoder layer (attention + MLP) using GLM-OCR's sandwich-norm pattern.
    ///
    /// Each layer carries four RMSNorms — one before and one after each
    /// sub-block:
    ///   - `input_layernorm`         — before attention
    ///   - `post_self_attn_layernorm` — after attention output (sandwich #2)
    ///   - `post_attention_layernorm` — before MLP (sandwich #3)
    ///   - `post_mlp_layernorm`       — after MLP output (sandwich #4)
    ///
    /// The residual is added around the *post*-norm output of each sub-block.
    #[derive(Debug, Clone)]
    struct DecoderLayer {
        self_attn: Attention,
        mlp: Mlp,
        input_layernorm: RmsNorm,
        post_self_attn_layernorm: RmsNorm,
        post_attention_layernorm: RmsNorm,
        post_mlp_layernorm: RmsNorm,
    }

    impl DecoderLayer {
        fn new(config: &DecoderConfig, vb: VarBuilder) -> Result<Self> {
            let input_layernorm = RmsNorm::new(config.hidden_size, config.rms_norm_eps, vb.pp("input_layernorm"))?;
            let self_attn = Attention::new(config, vb.pp("self_attn"))?;
            let post_self_attn_layernorm = RmsNorm::new(
                config.hidden_size,
                config.rms_norm_eps,
                vb.pp("post_self_attn_layernorm"),
            )?;
            let post_attention_layernorm = RmsNorm::new(
                config.hidden_size,
                config.rms_norm_eps,
                vb.pp("post_attention_layernorm"),
            )?;
            let mlp = Mlp::new(config, vb.pp("mlp"))?;
            let post_mlp_layernorm =
                RmsNorm::new(config.hidden_size, config.rms_norm_eps, vb.pp("post_mlp_layernorm"))?;

            Ok(Self {
                self_attn,
                mlp,
                input_layernorm,
                post_self_attn_layernorm,
                post_attention_layernorm,
                post_mlp_layernorm,
            })
        }

        fn reset_kv_cache(&mut self) {
            self.self_attn.reset_kv_cache();
        }

        /// Forward with explicit position_ids (M-RoPE path). Sandwich-norm: a
        /// norm both before and after each sub-block, with the residual added
        /// around the post-norm output.
        fn forward_with_position_ids(
            &mut self,
            xs: &Tensor,
            rope: &RotaryEmbedding,
            position_ids: &Tensor,
        ) -> Result<Tensor> {
            let residual = xs;
            let xs_norm = self.input_layernorm.forward(xs)?;
            let attn_out = self.self_attn.forward_with_position_ids(&xs_norm, rope, position_ids)?;
            let attn_post = self.post_self_attn_layernorm.forward(&attn_out)?;
            let xs = (residual + attn_post)?;

            let residual = &xs;
            let xs_norm = self.post_attention_layernorm.forward(&xs)?;
            let mlp_out = self.mlp.forward(&xs_norm)?;
            let mlp_post = self.post_mlp_layernorm.forward(&mlp_out)?;
            Ok((residual + mlp_post)?)
        }

        /// Backward-compat forward with seqlen_offset (text-only path).
        /// Mirrors the sandwich-norm structure of the M-RoPE path.
        #[allow(dead_code)]
        fn forward(&mut self, xs: &Tensor, rope: &RotaryEmbedding, seqlen_offset: usize) -> Result<Tensor> {
            let residual = xs;
            let xs_norm = self.input_layernorm.forward(xs)?;
            let attn_out = self.self_attn.forward(&xs_norm, rope, seqlen_offset)?;
            let attn_post = self.post_self_attn_layernorm.forward(&attn_out)?;
            let xs = (residual + attn_post)?;

            let residual = &xs;
            let xs_norm = self.post_attention_layernorm.forward(&xs)?;
            let mlp_out = self.mlp.forward(&xs_norm)?;
            let mlp_post = self.post_mlp_layernorm.forward(&mlp_out)?;
            Ok((residual + mlp_post)?)
        }
    }

    /// Embedding layer for token-to-embedding lookup.
    ///
    /// `vb` here is already the `embed_tokens` prefix root; `candle_nn::embedding`
    /// loads `<vb>.weight` directly, matching the upstream tensor name
    /// `model.language_model.embed_tokens.weight`.
    #[derive(Debug, Clone)]
    struct Embedding {
        embeddings: candle_nn::Embedding,
    }

    impl Embedding {
        fn new(vocab_size: usize, hidden_size: usize, vb: VarBuilder) -> Result<Self> {
            let embeddings = candle_nn::embedding(vocab_size, hidden_size, vb)?;
            Ok(Self { embeddings })
        }

        fn forward(&self, input_ids: &Tensor) -> Result<Tensor> {
            use candle_nn::Module;
            Ok(self.embeddings.forward(input_ids)?)
        }
    }

    /// In-tree GLM-4 decoder fork with `forward_embeds()` support.
    ///
    /// Exposes:
    /// - `forward()` — standard token-id path
    /// - `forward_embeds()` — embedding-level path with optional M-RoPE (skips embedding lookup)
    /// - `embed_tokens()` — exposes embedding for text-token embedding
    /// - `clear_kv_cache()` — resets KV cache
    pub struct Glm4Decoder {
        #[allow(dead_code)]
        pub(crate) config: DecoderConfig,
        embedding: Embedding,
        layers: Vec<DecoderLayer>,
        norm: RmsNorm,
        lm_head: candle_nn::Linear,
        rope: RotaryEmbedding,
    }

    impl Glm4Decoder {
        /// Create a new decoder from config and VarBuilders.
        ///
        /// `vb` is the trunk root — it must contain `embed_tokens`, `layers.*`,
        /// and `norm`. `lm_head_vb` is the LM head root (usually a sibling, not
        /// a child, of `vb`). For GLM-OCR the trunk lives at
        /// `language_model.model.*` while the head lives at
        /// `language_model.lm_head`.
        pub fn new(config: &DecoderConfig, vb: VarBuilder, lm_head_vb: VarBuilder) -> Result<Self> {
            let device = vb.device();
            let dtype = vb.dtype();

            let embedding = Embedding::new(config.vocab_size, config.hidden_size, vb.pp("embed_tokens"))?;

            let head_dim = if config.head_dim > 0 {
                config.head_dim
            } else {
                config.hidden_size / config.num_attention_heads
            };

            let rope = RotaryEmbedding::new(
                config.max_position_embeddings,
                head_dim,
                config.rope_theta(),
                config.mrope_section().to_vec(),
                dtype,
                device,
            )?;

            let mut layers = Vec::with_capacity(config.num_hidden_layers);
            let vb_layers = vb.pp("layers");
            for i in 0..config.num_hidden_layers {
                let layer = DecoderLayer::new(config, vb_layers.pp(i))?;
                layers.push(layer);
            }

            let norm = RmsNorm::new(config.hidden_size, config.rms_norm_eps, vb.pp("norm"))?;

            let lm_head = candle_nn::linear_no_bias(config.hidden_size, config.vocab_size, lm_head_vb)?;

            Ok(Self {
                config: config.clone(),
                embedding,
                layers,
                norm,
                lm_head,
                rope,
            })
        }

        /// Standard token-id forward path (mirrors upstream `Model::forward`).
        /// Uses default position_ids with seqlen_offset.
        pub fn forward(&mut self, input_ids: &Tensor, seqlen_offset: usize) -> Result<Tensor> {
            let input_embeds = self.embedding.forward(input_ids)?;
            let (batch_size, seq_len, _) = input_embeds.dims3()?;
            let dev = input_embeds.device();
            let position_ids = default_position_ids(batch_size, seq_len, seqlen_offset, dev)?;
            self.forward_embeds_internal(&input_embeds, &position_ids)
        }

        /// Embedding-level forward path with explicit position_ids for multimodal RoPE.
        /// Skips the input-token-embedding lookup and feeds `input_embeds` directly
        /// into the transformer stack.
        ///
        /// position_ids shape:
        ///   - (1, B, seq_len): standard 1-D positions (fallback)
        ///   - (3, B, seq_len): multimodal positions (temporal, height, width)
        ///
        /// This is the addition over upstream candle glm4 that motivates the
        /// in-tree fork. GLM-OCR concatenates vision-token embeddings with
        /// text-token embeddings, then calls this method with multimodal position_ids.
        pub fn forward_embeds(&mut self, input_embeds: &Tensor, position_ids: &Tensor) -> Result<Tensor> {
            self.forward_embeds_internal(input_embeds, position_ids)
        }

        /// Backward-compat forward_embeds with seqlen_offset (text-only path).
        /// Internally converts to default position_ids.
        pub fn forward_embeds_with_offset(&mut self, input_embeds: &Tensor, seqlen_offset: usize) -> Result<Tensor> {
            let (batch_size, seq_len, _) = input_embeds.dims3()?;
            let dev = input_embeds.device();
            let position_ids = default_position_ids(batch_size, seq_len, seqlen_offset, dev)?;
            self.forward_embeds_internal(input_embeds, &position_ids)
        }

        /// Embedding lookup for assembling vision-as-prefix input embeddings.
        ///
        /// Exposes the embedding layer so the engine can embed text tokens for
        /// concatenation with the vision prefix.
        pub fn embed_tokens(&self, input_ids: &Tensor) -> Result<Tensor> {
            self.embedding.forward(input_ids)
        }

        /// Clear cached KV state between independent inference calls.
        pub fn clear_kv_cache(&mut self) {
            for layer in &mut self.layers {
                layer.reset_kv_cache();
            }
        }

        fn forward_embeds_internal(&mut self, input_embeds: &Tensor, position_ids: &Tensor) -> Result<Tensor> {
            let mut xs = input_embeds.clone();

            for layer in &mut self.layers {
                xs = layer.forward_with_position_ids(&xs, &self.rope, position_ids)?;
            }

            xs = self.norm.forward(&xs)?;

            let seq_len = xs.dim(1)?;
            let logits = xs.i((.., seq_len - 1, ..))?.apply(&self.lm_head)?;

            Ok(logits)
        }
    }
}

#[cfg(not(target_arch = "wasm32"))]
pub use imp::Glm4Decoder;
