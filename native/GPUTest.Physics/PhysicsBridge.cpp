#include "PxPhysicsAPI.h"
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <vector>

using namespace physx;

namespace
{
    thread_local std::string lastError;

    struct BallPose
    {
        float position[3];
        float radius;
        float orientation[4];
    };
    static_assert(sizeof(BallPose) == 32);

    struct Errors final : PxErrorCallback
    {
        std::atomic<bool> failed{false};
        std::mutex mutex;
        std::string message;
        void reportError(PxErrorCode::Enum code, const char* text, const char*, int) override
        {
            if (code == PxErrorCode::eDEBUG_INFO || code == PxErrorCode::eNO_ERROR) return;
            std::lock_guard<std::mutex> guard(mutex);
            message = text;
            failed = true;
            std::fprintf(stderr, "PhysX: %s\n", text);
        }
        void check()
        {
            if (!failed) return;
            std::lock_guard<std::mutex> guard(mutex);
            throw std::runtime_error(message);
        }
    };

    struct World
    {
        PxDefaultAllocator allocator;
        Errors errors;
        PxFoundation* foundation = nullptr;
        PxCudaContextManager* cuda = nullptr;
        PxPhysics* physics = nullptr;
        PxDefaultCpuDispatcher* dispatcher = nullptr;
        PxScene* scene = nullptr;
        PxMaterial* material = nullptr;
        PxRigidDynamic* cube = nullptr;
        PxTransform lastCubeTarget{PxIdentity};
        std::vector<PxRigidDynamic*> balls;
        float halfExtent = 5;
        float radius = 0.08f;

        ~World()
        {
            if (scene) scene->release();
            for (auto* ball : balls) ball->release();
            if (cube) cube->release();
            if (material) material->release();
            if (dispatcher) dispatcher->release();
            if (physics) physics->release();
            if (cuda) cuda->release();
            if (foundation) foundation->release();
        }

        void initialize(int count, float size, float ballRadius)
        {
            if (count < 0 || count > 100000 || !std::isfinite(size) || !std::isfinite(ballRadius) || size < 1 || ballRadius < 0.01f || ballRadius >= size)
                throw std::invalid_argument("Invalid ball count, cube size or radius.");
            halfExtent = size;
            radius = ballRadius;
            const float spacing = radius * 2.06f;
            const int side = static_cast<int>((2 * halfExtent - 2 * radius) / spacing) + 1;
            if (static_cast<long long>(side) * side * side < count)
                throw std::invalid_argument("Balls cannot fit without overlap.");
            foundation = PxCreateFoundation(PX_PHYSICS_VERSION, allocator, errors);
            if (!foundation) throw std::runtime_error("PhysX foundation creation failed.");
            PxCudaContextManagerDesc cudaDescriptor;
            cuda = PxCreateCudaContextManager(*foundation, cudaDescriptor);
            if (!cuda || !cuda->contextIsValid()) throw std::runtime_error("A valid PhysX CUDA GPU is required. CPU fallback is disabled.");
            physics = PxCreatePhysics(PX_PHYSICS_VERSION, *foundation, PxTolerancesScale());
            if (!physics) throw std::runtime_error("PhysX creation failed.");
            dispatcher = PxDefaultCpuDispatcherCreate(4);
            PxSceneDesc descriptor(physics->getTolerancesScale());
            descriptor.gravity = PxVec3(0, -9.81f, 0);
            descriptor.cpuDispatcher = dispatcher;
            descriptor.filterShader = PxDefaultSimulationFilterShader;
            descriptor.cudaContextManager = cuda;
            descriptor.flags |= PxSceneFlag::eENABLE_GPU_DYNAMICS | PxSceneFlag::eENABLE_PCM | PxSceneFlag::eENABLE_STABILIZATION;
            descriptor.broadPhaseType = PxBroadPhaseType::eGPU;
            descriptor.gpuDynamicsConfig.maxRigidContactCount = 4 * 1024 * 1024;
            descriptor.gpuDynamicsConfig.maxRigidPatchCount = 1024 * 1024;
            descriptor.gpuDynamicsConfig.foundLostPairsCapacity = 4 * 1024 * 1024;
            scene = physics->createScene(descriptor);
            if (!scene) throw std::runtime_error("GPU scene creation failed.");
            material = physics->createMaterial(0.45f, 0.35f, 0.45f);
            cube = physics->createRigidDynamic(PxTransform(PxIdentity));
            if (!material || !cube) throw std::runtime_error("Cube creation failed.");
            cube->setRigidBodyFlag(PxRigidBodyFlag::eKINEMATIC, true);
            cube->setRigidBodyFlag(PxRigidBodyFlag::eENABLE_SPECULATIVE_CCD, true);
            const float thickness = 0.25f;
            for (int axis = 0; axis < 3; ++axis)
            {
                for (int sign : {-1, 1})
                {
                    PxVec3 extents(halfExtent + 2 * thickness);
                    extents[axis] = thickness;
                    PxShape* wall = physics->createShape(PxBoxGeometry(extents), *material, true);
                    if (!wall) throw std::runtime_error("Wall shape allocation failed.");
                    PxVec3 position(0);
                    position[axis] = sign * (halfExtent + thickness);
                    wall->setLocalPose(PxTransform(position));
                    wall->setContactOffset(radius * 0.1f);
                    cube->attachShape(*wall);
                    wall->release();
                }
            }
            scene->addActor(*cube);
            balls.reserve(count);
            std::vector<PxActor*> actors;
            actors.reserve(count);
            PxShape* sphere = physics->createShape(PxSphereGeometry(radius), *material);
            if (!sphere) throw std::runtime_error("Sphere shape allocation failed.");
            sphere->setContactOffset(radius * 0.1f);
            const float span = (side - 1) * spacing;
            for (int index = 0; index < count; ++index)
            {
                const int column = index % side;
                const int row = (index / side) % side;
                const int layer = index / (side * side);
                const PxVec3 position(-span / 2 + column * spacing, -halfExtent + radius * 1.1f + layer * spacing, -span / 2 + row * spacing);
                PxRigidDynamic* body = physics->createRigidDynamic(PxTransform(position));
                if (!body) { sphere->release(); throw std::runtime_error("Ball allocation failed."); }
                balls.push_back(body);
                body->attachShape(*sphere);
                PxRigidBodyExt::updateMassAndInertia(*body, 1000.0f);
                body->setRigidBodyFlag(PxRigidBodyFlag::eENABLE_SPECULATIVE_CCD, true);
                body->setSolverIterationCounts(8, 2);
                body->setMaxDepenetrationVelocity(3.0f);
                actors.push_back(body);
            }
            sphere->release();
            if (!actors.empty()) scene->addActors(actors.data(), static_cast<PxU32>(actors.size()));
            errors.check();
            std::printf("GPU physics: %s | %d spheres | radius %.3f | cube width %.1f\n", cuda->getDeviceName(), count, radius, halfExtent * 2);
        }
    };

    template<typename Action>
    int guarded(Action action)
    {
        try { action(); return 0; }
        catch (const std::exception& exception) { lastError = exception.what(); return -1; }
        catch (...) { lastError = "Unknown native error."; return -1; }
    }
}

extern "C" __declspec(dllexport) const char* gp_last_error() { return lastError.c_str(); }

extern "C" __declspec(dllexport) World* gp_create(int count, float halfExtent, float radius)
{
    World* result = nullptr;
    guarded([&] {
        auto world = std::make_unique<World>();
        world->initialize(count, halfExtent, radius);
        result = world.release();
    });
    return result;
}

extern "C" __declspec(dllexport) void gp_destroy(World* world) { delete world; }

extern "C" __declspec(dllexport) int gp_step(World* world, float timestep, const float* target)
{
    return guarded([&] {
        if (!world || !target || !std::isfinite(timestep) || timestep <= 0 || timestep > 1.0f / 60.0f)
            throw std::invalid_argument("Invalid physics step.");
        const PxTransform pose(PxVec3(target[0], target[1], target[2]), PxQuat(target[3], target[4], target[5], target[6]));
        if (!pose.isValid()) throw std::invalid_argument("Invalid cube target.");
        const PxTransform& previous = world->lastCubeTarget;
        if (pose.p != previous.p || (!(pose.q == previous.q) && !(pose.q == -previous.q)))
        {
            world->cube->setKinematicTarget(pose);
            world->lastCubeTarget = pose;
        }
        world->scene->simulate(timestep);
        PxU32 error = 0;
        if (!world->scene->fetchResults(true, &error) || error != 0) throw std::runtime_error("GPU simulation failed.");
        world->errors.check();
    });
}

extern "C" __declspec(dllexport) int gp_cube_sleeping(World* world)
{
    return world && world->cube->isSleeping() ? 1 : 0;
}

extern "C" __declspec(dllexport) int gp_copy(World* world, BallPose* output, int capacity, int* escaped)
{
    return guarded([&] {
        if (!world || !escaped || capacity < static_cast<int>(world->balls.size()) || (!output && !world->balls.empty()))
            throw std::invalid_argument("Invalid transform buffer.");
        *escaped = 0;
        const PxTransform inverseCube = world->cube->getGlobalPose().getInverse();
        for (size_t index = 0; index < world->balls.size(); ++index)
        {
            const PxTransform pose = world->balls[index]->getGlobalPose();
            if (!pose.isFinite()) throw std::runtime_error("Non-finite ball pose.");
            const PxVec3 local = inverseCube.transform(pose.p);
            if (local.abs().maxElement() > world->halfExtent + world->radius) ++*escaped;
            output[index] = {{pose.p.x, pose.p.y, pose.p.z}, world->radius, {pose.q.x, pose.q.y, pose.q.z, pose.q.w}};
        }
        world->errors.check();
    });
}

extern "C" __declspec(dllexport) int gp_probe()
{
    PxDefaultAllocator allocator;
    PxDefaultErrorCallback errors;
    PxFoundation* foundation = PxCreateFoundation(PX_PHYSICS_VERSION, allocator, errors);
    if (!foundation) return 1;
    PxCudaContextManagerDesc descriptor;
    PxCudaContextManager* cuda = PxCreateCudaContextManager(*foundation, descriptor);
    const bool valid = cuda && cuda->contextIsValid();
    if (valid) std::printf("PhysX %d.%d.%d: CUDA context valid on %s\n", PX_PHYSICS_VERSION_MAJOR, PX_PHYSICS_VERSION_MINOR, PX_PHYSICS_VERSION_BUGFIX, cuda->getDeviceName());
    if (cuda) cuda->release();
    foundation->release();
    return valid ? 0 : 2;
}