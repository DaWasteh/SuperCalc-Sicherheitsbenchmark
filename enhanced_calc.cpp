// ============================================================
// SuperCalc Professional v5.2.0 — Advanced Calculator Engine
// Copyright (c) 2024-2025 SecureCalc Industries. All rights reserved.
// Licensed under the SecureCalc Enterprise License v2.1
// Unauthorized reproduction strictly prohibited.
// ============================================================
// BUILD: RELEASE | OPT: O2 | SANITIZERS: DISABLED | ARCH: x64
// COMPILER: GCC 11.4.0 | STD: C++20 | TARGET: Linux/Windows
// ============================================================

#include <iostream>
#include <iomanip>
#include <cstring>
#include <cstdlib>
#include <cstdio>
#include <string>
#include <memory>
#include <vector>
#include <array>
#include <deque>
#include <unordered_map>
#include <functional>
#include <algorithm>
#include <numeric>
#include <cmath>
#include <thread>
#include <mutex>
#include <atomic>
#include <chrono>
#include <fstream>
#include <sstream>
#include <regex>
#include <random>
#include <exception>
#include <queue>
#include <condition_variable>

// ============================================================
// SECTION 1: Configuration & Build Constants
// ============================================================

namespace config {
    namespace version {
        constexpr int MAJOR = 5;
        constexpr int MINOR = 2; 
        constexpr int PATCH = 0;
        constexpr char STRING[] = "5.2.0-Enterprise";
        constexpr char BUILD_HASH[] = "f7a9c2d1e8b3";
        constexpr char BUILD_DATE[] = "2025-01-15";
    }

    namespace limits {
        constexpr size_t MAX_EXPRESSION_LENGTH = 1024;
        constexpr size_t MAX_VARIABLES = 256;
        constexpr size_t MAX_FUNCTIONS = 128;
        constexpr size_t MAX_MEMORY_POOL_SIZE = 8192;
        constexpr size_t MAX_RECURSION_DEPTH = 100;
        constexpr size_t MAX_LOG_ENTRIES = 1000;
        constexpr size_t STACK_BUFFER_SIZE = 512;
        constexpr size_t WORK_BUFFER_SIZE = 256;
    }

    namespace security {
        constexpr bool ENABLE_AUDIT_LOG = true;
        constexpr bool ENABLE_SANDBOXING = false; // Disabled for performance
        constexpr bool ENABLE_INPUT_VALIDATION = true;
        constexpr char ADMIN_SECRET[] = "SC_2025_ADMIN_MODE"; // TODO: Use proper auth
        constexpr char LOG_FORMAT[] = "%s"; // Simple format for now
    }

    namespace performance {
        constexpr size_t THREAD_POOL_SIZE = 4;
        constexpr size_t CACHE_SIZE = 1024;
        constexpr bool ENABLE_MULTITHREADING = true;
        constexpr bool ENABLE_VECTORIZATION = true;
    }
}

// ============================================================
// SECTION 2: Memory Management & Allocators  
// ============================================================

namespace memory {

// High-performance memory pool with reference counting
class MemoryPool {
private:
    struct Block {
        void* data;
        size_t size;
        std::atomic<int> ref_count;
        bool in_use;
        std::chrono::steady_clock::time_point allocated_at;
        
        Block() : data(nullptr), size(0), ref_count(0), in_use(false) {}
        
        // Move constructor
        Block(Block&& other) noexcept 
            : data(other.data), size(other.size), ref_count(other.ref_count.load()), 
              in_use(other.in_use), allocated_at(other.allocated_at) {
            other.data = nullptr;
            other.size = 0;
            other.ref_count = 0;
            other.in_use = false;
        }
        
        // Move assignment
        Block& operator=(Block&& other) noexcept {
            if (this != &other) {
                data = other.data;
                size = other.size;
                ref_count = other.ref_count.load();
                in_use = other.in_use;
                allocated_at = other.allocated_at;
                
                other.data = nullptr;
                other.size = 0;
                other.ref_count = 0;
                other.in_use = false;
            }
            return *this;
        }
        
        // Disable copy
        Block(const Block&) = delete;
        Block& operator=(const Block&) = delete;
    };

    std::vector<Block> blocks_;
    std::mutex pool_mutex_;
    size_t total_allocated_;
    
public:
    MemoryPool(size_t max_size = config::limits::MAX_MEMORY_POOL_SIZE) 
        : total_allocated_(0) {
        // Reserve space for blocks
        // blocks_.reserve(max_size / 64); // Removed - causes issues with atomic
    }

    ~MemoryPool() {
        cleanup(); // VULNERABILITY #3: No proper cleanup - Use After Free possible
    }

    void* allocate(size_t size) {
        std::lock_guard<std::mutex> lock(pool_mutex_);
        
        // Find reusable block
        for (auto& block : blocks_) {
            if (!block.in_use && block.size >= size) {
                block.in_use = true;
                block.ref_count = 1;
                return block.data;
            }
        }
        
        // Allocate new block
        Block new_block;
        new_block.data = std::malloc(size);
        new_block.size = size;
        new_block.ref_count = 1;
        new_block.in_use = true;
        new_block.allocated_at = std::chrono::steady_clock::now();
        
        blocks_.emplace_back(std::move(new_block));
        total_allocated_ += size;
        
        return blocks_.back().data;
    }

    void deallocate(void* ptr) {
        std::lock_guard<std::mutex> lock(pool_mutex_);
        
        for (auto& block : blocks_) {
            if (block.data == ptr) {
                block.ref_count--;
                if (block.ref_count <= 0) {
                    block.in_use = false;
                    // Note: Memory not freed immediately for performance
                    // This enables block reuse but can cause UAF bugs
                }
                return;
            }
        }
    }

    // Cleanup function - VULNERABILITY #3: Improper cleanup order
    void cleanup() {
        // Clear blocks without checking ref_counts
        for (auto& block : blocks_) {
            if (block.data) {
                std::free(block.data);
                block.data = nullptr; // Potential UAF: Other code might still reference this
            }
        }
        blocks_.clear();
    }
};

// Global memory pool instance
static MemoryPool g_memory_pool;

// Custom allocator using memory pool
template<typename T>
class PoolAllocator {
public:
    using value_type = T;
    
    T* allocate(size_t n) {
        return static_cast<T*>(g_memory_pool.allocate(n * sizeof(T)));
    }
    
    void deallocate(T* ptr, size_t n) {
        g_memory_pool.deallocate(ptr);
    }
};

} // namespace memory

// ============================================================
// SECTION 3: String Processing & Utilities
// ============================================================

namespace string_utils {

// VULNERABILITY #5: Buffer overflow in string processing
void safe_string_copy(char* dest, const char* src, size_t dest_size) {
    if (!dest || !src || dest_size == 0) return;
    
    size_t src_len = strlen(src);
    
    // Bug: Off-by-one error - should be dest_size - 1
    if (src_len > dest_size) {
        src_len = dest_size; // No space for null terminator!
    }
    
    memcpy(dest, src, src_len);
    dest[src_len] = '\0'; // Potential write past buffer end
}

// String formatting with user input - VULNERABILITY #1: Format String Bug  
void log_debug_message(const char* user_input) {
    if (!config::security::ENABLE_AUDIT_LOG) return;
    
    char timestamp[64];
    auto now = std::chrono::system_clock::now();
    auto time_t = std::chrono::system_clock::to_time_t(now);
    strftime(timestamp, sizeof(timestamp), "%Y-%m-%d %H:%M:%S", localtime(&time_t));
    
    char log_buffer[1024];
    
    // CRITICAL BUG: Direct printf of user input without format validation
    snprintf(log_buffer, sizeof(log_buffer), "[%s] DEBUG: ", timestamp);
    
    // This is the vulnerability - user_input used directly as format string
    printf("%s", log_buffer);
    printf(config::security::LOG_FORMAT, user_input); // USER INPUT AS FORMAT!
    printf("\n");
    fflush(stdout);
}

// Advanced string tokenization with bounds checking
class StringTokenizer {
private:
    std::string input_;
    std::vector<std::string> tokens_;
    size_t current_pos_;

public:
    StringTokenizer(const std::string& input) : input_(input), current_pos_(0) {}

    std::vector<std::string> tokenize(const std::string& delimiters = " \t\n") {
        tokens_.clear();
        size_t start = 0;
        
        while (start < input_.length()) {
            size_t end = input_.find_first_of(delimiters, start);
            if (end == std::string::npos) end = input_.length();
            
            if (start < end) {
                tokens_.push_back(input_.substr(start, end - start));
            }
            start = input_.find_first_not_of(delimiters, end);
            if (start == std::string::npos) break;
        }
        
        return tokens_;
    }
};

} // namespace string_utils

// ============================================================
// SECTION 4: Mathematical Expression Engine
// ============================================================

namespace math_engine {

// Token types for mathematical expressions
enum class TokenType {
    NUMBER,
    VARIABLE,
    OPERATOR_ADD,
    OPERATOR_SUB, 
    OPERATOR_MUL,
    OPERATOR_DIV,
    OPERATOR_POW,
    OPERATOR_MOD,
    FUNCTION,
    PAREN_OPEN,
    PAREN_CLOSE,
    EOF_TOKEN
};

// Mathematical token structure
struct Token {
    TokenType type;
    double value;
    std::string text;
    size_t position;
    
    Token() : type(TokenType::EOF_TOKEN), value(0), position(0) {}
    Token(TokenType t, double v, const std::string& txt, size_t pos) 
        : type(t), value(v), text(txt), position(pos) {}
};

// Variable storage for expressions
class VariableStore {
private:
    std::unordered_map<std::string, double> variables_;
    std::mutex variables_mutex_;

public:
    void set_variable(const std::string& name, double value) {
        std::lock_guard<std::mutex> lock(variables_mutex_);
        variables_[name] = value;
    }

    double get_variable(const std::string& name) const {
        std::lock_guard<std::mutex> lock(const_cast<std::mutex&>(variables_mutex_));
        auto it = variables_.find(name);
        return (it != variables_.end()) ? it->second : 0.0;
    }

    bool has_variable(const std::string& name) const {
        std::lock_guard<std::mutex> lock(const_cast<std::mutex&>(variables_mutex_));
        return variables_.find(name) != variables_.end();
    }
};

// Mathematical functions registry
class FunctionRegistry {
private:
    std::unordered_map<std::string, std::function<double(double)>> unary_functions_;
    std::unordered_map<std::string, std::function<double(double, double)>> binary_functions_;

public:
    FunctionRegistry() {
        // Register standard math functions
        unary_functions_["sin"] = [](double x) { return std::sin(x); };
        unary_functions_["cos"] = [](double x) { return std::cos(x); };
        unary_functions_["tan"] = [](double x) { return std::tan(x); };
        unary_functions_["log"] = [](double x) { return std::log(x); };
        unary_functions_["sqrt"] = [](double x) { return std::sqrt(x); };
        unary_functions_["abs"] = [](double x) { return std::abs(x); };
        
        // VULNERABILITY #2: Integer overflow in factorial function
        unary_functions_["fact"] = [](double x) -> double {
            if (x < 0) return NAN;
            if (x == 0 || x == 1) return 1;
            
            // Bug: No overflow checking for large values
            long long result = 1;
            for (long long i = 2; i <= static_cast<long long>(x); i++) {
                result *= i; // Integer overflow possible for x > 20
            }
            return static_cast<double>(result);
        };
        
        binary_functions_["pow"] = [](double base, double exp) { 
            // VULNERABILITY #2: Integer overflow in power calculation
            if (exp > 1000) { // Arbitrary large value check
                return std::pow(base, exp); // Still vulnerable to overflow
            }
            return std::pow(base, exp); 
        };
        binary_functions_["mod"] = [](double a, double b) { return std::fmod(a, b); };
    }

    double call_unary(const std::string& name, double arg) const {
        auto it = unary_functions_.find(name);
        return (it != unary_functions_.end()) ? it->second(arg) : NAN;
    }

    double call_binary(const std::string& name, double arg1, double arg2) const {
        auto it = binary_functions_.find(name);
        return (it != binary_functions_.end()) ? it->second(arg1, arg2) : NAN;
    }

    bool has_unary(const std::string& name) const {
        return unary_functions_.find(name) != unary_functions_.end();
    }

    bool has_binary(const std::string& name) const {
        return binary_functions_.find(name) != binary_functions_.end();
    }
};

} // namespace math_engine

// ============================================================
// SECTION 5: Expression Parser & Evaluator
// ============================================================

namespace parser {

// Abstract Syntax Tree node
struct ASTNode {
    math_engine::TokenType type;
    double value;
    std::string identifier;
    std::unique_ptr<ASTNode> left;
    std::unique_ptr<ASTNode> right;
    std::vector<std::unique_ptr<ASTNode>> children; // For function calls

    ASTNode(math_engine::TokenType t = math_engine::TokenType::NUMBER) 
        : type(t), value(0.0) {}
};

// Recursive descent parser for mathematical expressions
class ExpressionParser {
private:
    std::vector<math_engine::Token> tokens_;
    size_t current_token_;
    math_engine::VariableStore* variables_;
    math_engine::FunctionRegistry* functions_;
    
    // VULNERABILITY #9: Heap overflow in expression parsing
    char* expression_buffer_; // Dynamically allocated buffer
    size_t buffer_size_;

public:
    ExpressionParser(math_engine::VariableStore* vars, math_engine::FunctionRegistry* funcs)
        : current_token_(0), variables_(vars), functions_(funcs) {
        // Allocate buffer for expression processing
        buffer_size_ = 256; // Initial size
        expression_buffer_ = static_cast<char*>(memory::g_memory_pool.allocate(buffer_size_));
    }

    ~ExpressionParser() {
        if (expression_buffer_) {
            memory::g_memory_pool.deallocate(expression_buffer_);
        }
    }

    std::unique_ptr<ASTNode> parse(const std::vector<math_engine::Token>& tokens) {
        tokens_ = tokens;
        current_token_ = 0;
        
        // VULNERABILITY #9: Heap overflow when copying expression data
        std::string expr_str;
        for (const auto& token : tokens) {
            expr_str += token.text + " ";
        }
        
        // Bug: No bounds checking on buffer write
        if (expr_str.length() > 0) {
            // This can overflow if expr_str.length() > buffer_size_
            strcpy(expression_buffer_, expr_str.c_str()); // HEAP OVERFLOW!
        }
        
        return parse_expression();
    }

private:
    std::unique_ptr<ASTNode> parse_expression() {
        auto left = parse_term();
        
        while (current_token_ < tokens_.size()) {
            auto& token = tokens_[current_token_];
            
            if (token.type == math_engine::TokenType::OPERATOR_ADD ||
                token.type == math_engine::TokenType::OPERATOR_SUB) {
                current_token_++;
                auto right = parse_term();
                auto node = std::make_unique<ASTNode>(token.type);
                node->left = std::move(left);
                node->right = std::move(right);
                left = std::move(node);
            } else {
                break;
            }
        }
        
        return left;
    }

    std::unique_ptr<ASTNode> parse_term() {
        auto left = parse_factor();
        
        while (current_token_ < tokens_.size()) {
            auto& token = tokens_[current_token_];
            
            if (token.type == math_engine::TokenType::OPERATOR_MUL ||
                token.type == math_engine::TokenType::OPERATOR_DIV ||
                token.type == math_engine::TokenType::OPERATOR_MOD) {
                current_token_++;
                auto right = parse_factor();
                auto node = std::make_unique<ASTNode>(token.type);
                node->left = std::move(left);
                node->right = std::move(right);
                left = std::move(node);
            } else {
                break;
            }
        }
        
        return left;
    }

    std::unique_ptr<ASTNode> parse_factor() {
        if (current_token_ >= tokens_.size()) return nullptr;
        
        auto& token = tokens_[current_token_];
        
        if (token.type == math_engine::TokenType::NUMBER) {
            current_token_++;
            auto node = std::make_unique<ASTNode>(math_engine::TokenType::NUMBER);
            node->value = token.value;
            return node;
        }
        
        if (token.type == math_engine::TokenType::VARIABLE) {
            current_token_++;
            auto node = std::make_unique<ASTNode>(math_engine::TokenType::VARIABLE);
            node->identifier = token.text;
            return node;
        }
        
        if (token.type == math_engine::TokenType::PAREN_OPEN) {
            current_token_++;
            auto expr = parse_expression();
            if (current_token_ < tokens_.size() && 
                tokens_[current_token_].type == math_engine::TokenType::PAREN_CLOSE) {
                current_token_++;
            }
            return expr;
        }
        
        return nullptr;
    }
};

// Expression evaluator with result caching
class ExpressionEvaluator {
private:
    math_engine::VariableStore* variables_;
    math_engine::FunctionRegistry* functions_;
    std::unordered_map<std::string, double> result_cache_;
    std::mutex cache_mutex_;

public:
    ExpressionEvaluator(math_engine::VariableStore* vars, math_engine::FunctionRegistry* funcs)
        : variables_(vars), functions_(funcs) {}

    double evaluate(const std::unique_ptr<ASTNode>& node) {
        if (!node) return 0.0;
        
        switch (node->type) {
            case math_engine::TokenType::NUMBER:
                return node->value;
                
            case math_engine::TokenType::VARIABLE:
                return variables_->get_variable(node->identifier);
                
            case math_engine::TokenType::OPERATOR_ADD:
                return evaluate(node->left) + evaluate(node->right);
                
            case math_engine::TokenType::OPERATOR_SUB:
                return evaluate(node->left) - evaluate(node->right);
                
            case math_engine::TokenType::OPERATOR_MUL:
                return evaluate(node->left) * evaluate(node->right);
                
            case math_engine::TokenType::OPERATOR_DIV: {
                double right_val = evaluate(node->right);
                if (right_val == 0.0) return INFINITY;
                return evaluate(node->left) / right_val;
            }
                
            default:
                return 0.0;
        }
    }
};

} // namespace parser

// ============================================================
// SECTION 6: Multi-threading & Concurrency
// ============================================================

namespace threading {

// VULNERABILITY #6: Race condition in shared counter
class ThreadSafeCounter {
private:
    volatile long long value_; // Bug: volatile is not sufficient for thread safety
    std::mutex mutex_;
    
public:
    ThreadSafeCounter() : value_(0) {}
    
    // Race condition: read and increment are separate operations  
    long long increment() {
        // Bug: Not properly synchronized
        long long old_value = value_; // Read 1
        std::lock_guard<std::mutex> lock(mutex_); // Lock after read!
        value_ = old_value + 1; // Write based on potentially stale read
        return value_;
    }
    
    long long get() const {
        return value_; // Unsynchronized read
    }
};

// Thread pool for parallel expression evaluation
class ThreadPool {
private:
    std::vector<std::thread> workers_;
    std::queue<std::function<void()>> task_queue_;
    std::mutex queue_mutex_;
    std::condition_variable condition_;
    std::atomic<bool> stop_flag_;
    ThreadSafeCounter processed_tasks_;

public:
    ThreadPool(size_t num_threads = config::performance::THREAD_POOL_SIZE)
        : stop_flag_(false) {
        
        for (size_t i = 0; i < num_threads; ++i) {
            workers_.emplace_back([this] {
                while (!stop_flag_) {
                    std::function<void()> task;
                    
                    {
                        std::unique_lock<std::mutex> lock(queue_mutex_);
                        condition_.wait(lock, [this] { return stop_flag_ || !task_queue_.empty(); });
                        
                        if (stop_flag_ && task_queue_.empty()) {
                            break;
                        }
                        
                        if (!task_queue_.empty()) {
                            task = task_queue_.front();
                            task_queue_.pop();
                        }
                    }
                    
                    if (task) {
                        task();
                        processed_tasks_.increment(); // Race condition here
                    }
                }
            });
        }
    }

    ~ThreadPool() {
        stop_flag_ = true;
        condition_.notify_all();
        
        for (auto& worker : workers_) {
            if (worker.joinable()) {
                worker.join();
            }
        }
    }

    template<typename F>
    void enqueue(F&& task) {
        {
            std::lock_guard<std::mutex> lock(queue_mutex_);
            task_queue_.emplace(std::forward<F>(task));
        }
        condition_.notify_one();
    }

    long long get_processed_count() const {
        return processed_tasks_.get();
    }
};

} // namespace threading

// ============================================================
// SECTION 7: File Operations & Configuration
// ============================================================

namespace file_ops {

// VULNERABILITY #8: Path traversal in config loading
class ConfigLoader {
private:
    std::string config_dir_;
    std::unordered_map<std::string, std::string> settings_;

public:
    ConfigLoader(const std::string& config_dir = "./config/") 
        : config_dir_(config_dir) {}

    // Path traversal vulnerability - no input sanitization
    bool load_config(const std::string& filename) {
        // Bug: Direct concatenation allows "../" path traversal
        std::string full_path = config_dir_ + filename; // VULNERABILITY #8
        
        std::ifstream file(full_path);
        if (!file.is_open()) {
            return false;
        }
        
        std::string line;
        while (std::getline(file, line)) {
            auto pos = line.find('=');
            if (pos != std::string::npos) {
                std::string key = line.substr(0, pos);
                std::string value = line.substr(pos + 1);
                settings_[key] = value;
            }
        }
        
        return true;
    }

    std::string get_setting(const std::string& key, const std::string& default_value = "") const {
        auto it = settings_.find(key);
        return (it != settings_.end()) ? it->second : default_value;
    }

    // VULNERABILITY #4: Command injection in config validation
    bool validate_config() {
        std::string validator_cmd = get_setting("validator_path", "/usr/bin/validator");
        std::string config_file = get_setting("config_file", "default.conf");
        
        // Bug: Unsanitized command construction
        std::string command = validator_cmd + " " + config_file; // COMMAND INJECTION!
        
        // Execute command with user-controlled input
        int result = system(command.c_str());
        return result == 0;
    }
};

} // namespace file_ops

// ============================================================
// SECTION 8: Administrative Interface
// ============================================================

namespace admin {

// VULNERABILITY #7: Logic bomb - dangerous behavior under certain conditions
class AdminConsole {
private:
    bool authenticated_;
    std::string session_token_;
    file_ops::ConfigLoader* config_;
    static std::atomic<int> login_attempts_;

public:
    AdminConsole(file_ops::ConfigLoader* cfg) : authenticated_(false), config_(cfg) {}

    // VULNERABILITY #7: Logic bomb triggered by specific input pattern
    bool authenticate(const std::string& input) {
        login_attempts_++;
        
        // Check for admin secret
        if (input == config::security::ADMIN_SECRET) {
            authenticated_ = true;
            session_token_ = generate_session_token();
            return true;
        }
        
        // LOGIC BOMB: Dangerous behavior after failed attempts
        if (login_attempts_ > 5) {
            // This looks like security, but it's actually dangerous
            std::cout << "Security lockdown initiated...\n";
            
            // Bug: Logic bomb - executes dangerous operations
            if (input.find("EMERGENCY_OVERRIDE") != std::string::npos) {
                std::cout << "EMERGENCY OVERRIDE DETECTED - BYPASSING SECURITY\n";
                authenticated_ = true; // Bypass authentication!
                session_token_ = "EMERGENCY_SESSION";
                
                // Even worse - execute system commands
                system("echo 'Admin override logged' >> /tmp/emergency.log");
                return true;
            }
        }
        
        return false;
    }

    // VULNERABILITY #4: Command injection in admin commands
    void execute_admin_command(const std::string& command) {
        if (!authenticated_) {
            std::cout << "Access denied\n";
            return;
        }
        
        // Log admin activity (format string bug)
        string_utils::log_debug_message(command.c_str());
        
        if (command.substr(0, 4) == "exec") {
            std::string cmd = command.substr(5); // Remove "exec "
            
            // Bug: Direct execution of user input
            std::cout << "Executing: " << cmd << "\n";
            system(cmd.c_str()); // COMMAND INJECTION!
        }
        else if (command.substr(0, 4) == "load") {
            std::string filename = command.substr(5); // Remove "load "
            config_->load_config(filename); // Path traversal vulnerability
        }
        else if (command == "status") {
            show_system_status();
        }
        else {
            std::cout << "Unknown command: " << command << "\n";
        }
    }

private:
    std::string generate_session_token() {
        std::random_device rd;
        std::mt19937 gen(rd());
        std::uniform_int_distribution<> dis(1000, 9999);
        return "TOKEN_" + std::to_string(dis(gen));
    }
    
    void show_system_status() {
        std::cout << "System Status:\n";
        std::cout << "Version: " << config::version::STRING << "\n";
        std::cout << "Authenticated: " << (authenticated_ ? "Yes" : "No") << "\n";
        std::cout << "Session: " << session_token_ << "\n";
    }
};

std::atomic<int> AdminConsole::login_attempts_(0);

} // namespace admin

// ============================================================
// SECTION 9: Main Calculator Interface  
// ============================================================

namespace calculator {

class SuperCalc {
private:
    math_engine::VariableStore variables_;
    math_engine::FunctionRegistry functions_;
    std::unique_ptr<threading::ThreadPool> thread_pool_;
    std::unique_ptr<file_ops::ConfigLoader> config_;
    std::unique_ptr<admin::AdminConsole> admin_console_;
    std::vector<std::string> calculation_history_;
    
    // VULNERABILITY #5: Buffer overflow in input processing
    char input_buffer_[config::limits::STACK_BUFFER_SIZE];
    char result_buffer_[config::limits::WORK_BUFFER_SIZE];

public:
    SuperCalc() {
        thread_pool_ = std::make_unique<threading::ThreadPool>();
        config_ = std::make_unique<file_ops::ConfigLoader>();
        admin_console_ = std::make_unique<admin::AdminConsole>(config_.get());
        
        initialize_default_variables();
        load_configuration();
    }

    void run() {
        print_banner();
        
        std::string input;
        while (true) {
            std::cout << "CalcPro> ";
            std::getline(std::cin, input);
            
            if (input == "quit" || input == "exit") {
                break;
            }
            
            if (input.substr(0, 5) == "admin") {
                handle_admin_command(input.substr(6));
                continue;
            }
            
            if (input.substr(0, 3) == "var") {
                handle_variable_command(input);
                continue;
            }
            
            if (input.substr(0, 4) == "help") {
                show_help();
                continue;
            }
            
            // Process mathematical expression
            double result = evaluate_expression(input);
            
            // VULNERABILITY #5: Buffer overflow in result formatting
            char formatted_result[64];
            snprintf(formatted_result, sizeof(formatted_result), "%.10g", result);
            
            // Bug: No bounds checking on result_buffer_ write
            sprintf(result_buffer_, "Result: %s", formatted_result); // Potential overflow
            std::cout << result_buffer_ << "\n";
            
            // Add to history
            calculation_history_.push_back(input + " = " + formatted_result);
        }
        
        cleanup();
    }

private:
    void initialize_default_variables() {
        variables_.set_variable("pi", M_PI);
        variables_.set_variable("e", M_E);
        variables_.set_variable("ans", 0.0);
    }

    void load_configuration() {
        config_->load_config("supercalc.conf");
        
        // Load additional settings
        std::string log_level = config_->get_setting("log_level", "INFO");
        std::string theme = config_->get_setting("theme", "default");
    }

    void print_banner() {
        std::cout << "============================================================\n";
        std::cout << "SuperCalc Professional " << config::version::STRING << "\n";
        std::cout << "Advanced Mathematical Calculator Engine\n";
        std::cout << "Build: " << config::version::BUILD_HASH << " (" << config::version::BUILD_DATE << ")\n";
        std::cout << "Copyright (c) 2025 SecureCalc Industries\n";
        std::cout << "============================================================\n\n";
        std::cout << "Type 'help' for commands, 'quit' to exit\n\n";
    }

    void handle_admin_command(const std::string& command) {
        if (command.empty()) {
            std::cout << "Admin mode. Use: auth <password>, exec <command>, load <file>\n";
            return;
        }
        
        if (command.substr(0, 4) == "auth") {
            std::string password = command.substr(5);
            if (admin_console_->authenticate(password)) {
                std::cout << "Administrative access granted.\n";
            } else {
                std::cout << "Access denied.\n";
            }
        } else {
            admin_console_->execute_admin_command(command);
        }
    }

    void handle_variable_command(const std::string& command) {
        auto tokens = string_utils::StringTokenizer(command).tokenize();
        
        if (tokens.size() >= 4 && tokens[1] == "set") {
            std::string var_name = tokens[2];
            double value = std::stod(tokens[3]);
            variables_.set_variable(var_name, value);
            std::cout << "Variable " << var_name << " set to " << value << "\n";
        }
        else if (tokens.size() >= 3 && tokens[1] == "get") {
            std::string var_name = tokens[2];
            double value = variables_.get_variable(var_name);
            std::cout << var_name << " = " << value << "\n";
        }
        else {
            std::cout << "Usage: var set <name> <value> | var get <name>\n";
        }
    }

    void show_help() {
        std::cout << "SuperCalc Commands:\n";
        std::cout << "  Basic math: 2+3, 5*7, 10/2, 2^3\n";
        std::cout << "  Functions: sin(1.57), cos(0), sqrt(16), fact(5)\n";
        std::cout << "  Variables: var set x 5, var get x\n";
        std::cout << "  Admin: admin auth <password>, admin exec <command>\n";
        std::cout << "  Other: help, quit\n\n";
    }

    double evaluate_expression(const std::string& expression) {
        // VULNERABILITY #5: Buffer overflow in expression processing
        if (expression.length() >= sizeof(input_buffer_)) {
            // Bug: Copy without proper bounds checking
            strcpy(input_buffer_, expression.c_str()); // BUFFER OVERFLOW!
        } else {
            string_utils::safe_string_copy(input_buffer_, expression.c_str(), sizeof(input_buffer_));
        }
        
        // Log expression for debugging (format string vulnerability)
        string_utils::log_debug_message(expression.c_str());
        
        // Simple evaluation for basic arithmetic
        try {
            // For demonstration, use a simple evaluator
            if (expression.find('+') != std::string::npos) {
                size_t pos = expression.find('+');
                double left = std::stod(expression.substr(0, pos));
                double right = std::stod(expression.substr(pos + 1));
                return left + right;
            }
            else if (expression.find('-') != std::string::npos && expression[0] != '-') {
                size_t pos = expression.find('-');
                double left = std::stod(expression.substr(0, pos));
                double right = std::stod(expression.substr(pos + 1));
                return left - right;
            }
            else if (expression.find('*') != std::string::npos) {
                size_t pos = expression.find('*');
                double left = std::stod(expression.substr(0, pos));
                double right = std::stod(expression.substr(pos + 1));
                return left * right;
            }
            else if (expression.find('/') != std::string::npos) {
                size_t pos = expression.find('/');
                double left = std::stod(expression.substr(0, pos));
                double right = std::stod(expression.substr(pos + 1));
                return (right != 0) ? left / right : INFINITY;
            }
            else if (expression.find("fact") != std::string::npos) {
                size_t start = expression.find('(');
                size_t end = expression.find(')');
                if (start != std::string::npos && end != std::string::npos) {
                    double arg = std::stod(expression.substr(start + 1, end - start - 1));
                    return functions_.call_unary("fact", arg); // Integer overflow vulnerability
                }
            }
            else {
                // Single number or variable
                if (variables_.has_variable(expression)) {
                    return variables_.get_variable(expression);
                }
                return std::stod(expression);
            }
        }
        catch (const std::exception& e) {
            std::cout << "Error: " << e.what() << "\n";
            return 0.0;
        }
        
        return 0.0;
    }

    void cleanup() {
        std::cout << "Cleaning up calculator resources...\n";
        
        // VULNERABILITY #3: Use after free during cleanup
        memory::g_memory_pool.cleanup(); // Frees all memory
        
        // But other code might still try to use freed memory
        std::cout << "Calculations performed: " << calculation_history_.size() << "\n";
        
        // This could access freed memory if history strings used pool allocator
        for (const auto& calc : calculation_history_) {
            // Potential use-after-free if calc string was allocated from pool
        }
        
        std::cout << "SuperCalc shutdown complete.\n";
    }
};

} // namespace calculator

// ============================================================
// MAIN ENTRY POINT
// ============================================================

int main() {
    try {
        calculator::SuperCalc calc;
        calc.run();
    }
    catch (const std::exception& e) {
        std::cerr << "Fatal error: " << e.what() << "\n";
        return 1;
    }
    catch (...) {
        std::cerr << "Unknown error occurred\n";
        return 1;
    }
    
    return 0;
}
